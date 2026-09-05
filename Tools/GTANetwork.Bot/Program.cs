using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using GTANetworkShared;
using GTANetworkShared.Crypto;
using GTANetworkServer.Crypto;
using Lidgren.Network;
using ProtoBuf;

namespace GTANetwork.Bot;

// Same contract as Client/Misc/ChatData.cs and Server/Constant/ChatData.cs (neither lives in Shared).
[ProtoContract]
public class ChatData
{
    [ProtoMember(1)] public long Id { get; set; }
    [ProtoMember(2)] public string Sender { get; set; }
    [ProtoMember(3)] public string Message { get; set; }
}

internal sealed class Options
{
    public string Host = "127.0.0.1";
    public int Port = 4499;
    public string Name = "Bot";
    public string Password = "";
    public List<string> Say = new();
    public List<string> Expect = new();
    public List<(string Name, string Json)> Rpc = new();       // --rpc name json: RPC calls sent after the chat lines, 1 s apart
    public (string Name, int Count)? RpcBurst;                 // --rpc-burst name count: that many calls at once (rate limit test)
    public bool NoEncryption;                                  // --no-encryption: hail without a key (an old client); the server refuses it by default
    public string Pin;                                         // --pin <hex>: the server's X25519 public key that must match, else the bot leaves
    public double Duration = 5;      // seconds to stay connected after the last message was sent
    public double Timeout = 60;      // overall limit
    public bool Sync = true;
    public bool Discover;
    public bool Verbose;
    public bool Interactive;
    public string DownloadFiles;     // folder that receives the resources' <file>s, like the game's resources folder

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--host": o.Host = Next(); break;
                case "--port": o.Port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--name": o.Name = Next(); break;
                case "--password": o.Password = Next(); break;
                case "--say": o.Say.Add(Next()); break;
                case "--expect": o.Expect.Add(Next()); break;
                case "--rpc": { var name = Next(); o.Rpc.Add((name, Next())); break; }
                case "--rpc-burst": { var name = Next(); o.RpcBurst = (name, int.Parse(Next(), CultureInfo.InvariantCulture)); break; }
                case "--no-encryption": o.NoEncryption = true; break;
                case "--pin": o.Pin = Next(); break;
                case "--duration": o.Duration = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--timeout": o.Timeout = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--no-sync": o.Sync = false; break;
                case "--discover": o.Discover = true; break;
                case "--download-files": o.DownloadFiles = Next(); break;
                case "-i": case "--interactive": o.Interactive = true; break;
                case "-v": case "--verbose": o.Verbose = true; break;
                case "-h": case "--help":
                    Console.WriteLine(Usage);
                    Environment.Exit(0);
                    break;
                default: throw new ArgumentException("Unknown argument: " + args[i] + Environment.NewLine + Usage);
            }
        }
        return o;
    }

    public const string Usage = @"GTANetwork.Bot - headless GTA Network client

  --host <ip>          server address (default 127.0.0.1)
  --port <port>        server port (default 4499)
  --name <name>        player name (default Bot)
  --password <pass>    server password
  --say <text>         chat line or /command to send after joining (repeatable, 1 s apart)
  --expect <text>      substring that must appear in received chat or RPC results; exit code 1 otherwise (repeatable)
  --rpc <name> <json>  RPC call to the server after the chat lines (repeatable); the result is logged as
                       rpc <name> ok <json>  or  rpc <name> error <code>: <message>  and matched by --expect
  --rpc-burst <name> <n>  send n RPC calls at once (rate limit test)
  --no-encryption      connect like a client without the session handshake (the server refuses it unless RequireEncryption is off)
  --pin <hex>          the server public key (64 hex characters) that must match, else the bot leaves with: server key mismatch
  --duration <sec>     stay connected this long after the last --say (default 5)
  --timeout <sec>      give up after this long (default 60)
  --no-sync            do not send position sync packets
  --discover           send a LAN discovery request first and print the answers
  --download-files <dir>
                       fetch the resources' <file>s like the game does (HTTP file server: manifest.json and
                       /<resource>/<path>; otherwise the UDP stream) into <dir>/<resource>/<path>; exit code 1
                       when a file fails
  -i, --interactive    after joining, read chat lines / commands from stdin until /quit or EOF
  -v, --verbose        print Lidgren debug messages and raw packet sizes";
}

internal static class Program
{
    private static readonly Regex ColorCodes = new(@"~[a-zA-Z_]{1,2}~|~n~", RegexOptions.Compiled);

    private static Options _o;
    private static NetClient _client;
    private static int _myHandle;
    private static bool _joined, _confirmedFalseSent;
    private static readonly StringBuilder _chatLog = new();
    private static Download _download;
    private static readonly HashSet<string> _resourcesWithScripts = new();
    private static int _fileFailures;
    private static Vector3 _position = new(0f, 0f, 72f);
    private static float _heading;
    private static readonly Stopwatch _clock = Stopwatch.StartNew();
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _stdin = new();
    private static volatile bool _stdinClosed;

    private sealed class Download
    {
        public int Id;
        public FileType Type;
        public string Name, Resource, Hash;
        public int Length;
        public MemoryStream Data = new();
    }

    private static int Main(string[] args)
    {
        try
        {
            _o = Options.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var config = new NetPeerConfiguration("GTANETWORK") { ConnectionTimeout = 30f };
        config.EnableMessageType(NetIncomingMessageType.ConnectionLatencyUpdated);
        config.EnableMessageType(NetIncomingMessageType.DiscoveryResponse);
        if (_o.Verbose)
        {
            config.EnableMessageType(NetIncomingMessageType.DebugMessage);
            config.EnableMessageType(NetIncomingMessageType.VerboseDebugMessage);
        }

        AesGcmNet.Install();
        _client = new NetClient(config);
        _client.Start();
        if (!_o.NoEncryption) _handshakeKey = KeyPair.Generate();
        _pin = SessionHandshake.FromHex(_o.Pin, 32);
        if (_o.Pin != null && _pin == null) { Log("crypto", "--pin must be 64 hex characters"); return 1; }

        if (_o.Discover)
        {
            Log("discovery", $"asking {_o.Host}:{_o.Port} who is there ...");
            _client.DiscoverKnownPeer(_o.Host, _o.Port);
            var until = _clock.Elapsed + TimeSpan.FromSeconds(2);
            while (_clock.Elapsed < until) Pump();
        }

        var version = ParseableVersion.FromAssembly(typeof(ConnectionRequest).Assembly);
        var request = new ConnectionRequest
        {
            SocialClubName = _o.Name,
            DisplayName = _o.Name,
            ScriptVersion = version.ToString(),
            GameVersion = 0,
            CEF = false,
            CEFDevtool = false,
            MediaStream = false,
            Password = string.IsNullOrEmpty(_o.Password) ? null : _o.Password,
            ClientPublicKey = _handshakeKey?.PublicKey,
        };

        var hail = _client.CreateMessage();
        WritePacket(hail, PacketType.ConnectionRequest, request);
        Log("connect", $"{_o.Host}:{_o.Port} as \"{_o.Name}\" (client version {version})");
        _client.Connect(_o.Host, _o.Port, hail);

        var deadline = _o.Interactive ? TimeSpan.MaxValue : _clock.Elapsed + TimeSpan.FromSeconds(_o.Timeout);
        var sayIndex = 0;
        // The scripted steps, one second apart, in this order: chat lines, RPC calls, the RPC burst.
        var steps = new List<Action>();
        foreach (var text in _o.Say) { var line = text; steps.Add(() => SendChat(line)); }
        foreach (var (name, json) in _o.Rpc) { var n = name; var j = json; steps.Add(() => SendRpc(n, j)); }
        if (_o.RpcBurst is { } burst) steps.Add(() => { for (var i = 0; i < burst.Count; i++) SendRpc(burst.Name, "null"); });
        TimeSpan nextSay = TimeSpan.Zero, nextSync = TimeSpan.Zero, stayUntil = TimeSpan.MaxValue;
        var disconnected = false;

        if (_o.Interactive)
        {
            new Thread(() =>
            {
                string line;
                while ((line = Console.ReadLine()) != null) _stdin.Enqueue(line);
                _stdinClosed = true;
            }) { IsBackground = true, Name = "stdin" }.Start();
            Log("interactive", "type chat lines or /commands, /quit to leave");
        }

        while (_clock.Elapsed < deadline && !disconnected)
        {
            disconnected = Pump();

            if (!_joined) continue;

            if (sayIndex < steps.Count && _clock.Elapsed >= nextSay)
            {
                steps[sayIndex++]();
                nextSay = _clock.Elapsed + TimeSpan.FromSeconds(1);
                if (sayIndex == steps.Count && !_o.Interactive) stayUntil = _clock.Elapsed + TimeSpan.FromSeconds(_o.Duration);
            }
            else if (steps.Count == 0 && stayUntil == TimeSpan.MaxValue && !_o.Interactive)
            {
                stayUntil = _clock.Elapsed + TimeSpan.FromSeconds(_o.Duration);
            }

            if (_o.Interactive)
            {
                while (_stdin.TryDequeue(out var line))
                {
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    if (line == "/quit" || line == "/exit") { stayUntil = _clock.Elapsed + TimeSpan.FromSeconds(0.7); break; } // let replies arrive
                    SendChat(line);
                }
                if (_stdinClosed && _stdin.IsEmpty && sayIndex >= steps.Count && stayUntil == TimeSpan.MaxValue) stayUntil = _clock.Elapsed + TimeSpan.FromSeconds(0.7);
            }

            if (_o.Sync && _clock.Elapsed >= nextSync)
            {
                SendPureSync();
                nextSync = _clock.Elapsed + TimeSpan.FromMilliseconds(100);
            }

            if (_clock.Elapsed >= stayUntil) break;
        }

        if (!disconnected)
        {
            _client.Disconnect("Quit");
            var until = _clock.Elapsed + TimeSpan.FromSeconds(1);
            while (_clock.Elapsed < until) Pump();
        }
        _client.Shutdown("bye");

        var chat = _chatLog.ToString();
        var missing = _o.Expect.Where(e => !chat.Contains(e, StringComparison.Ordinal)).ToList();

        Console.WriteLine();
        if (!_joined)
        {
            Log("result", "FAILED: never joined the server");
            return 1;
        }
        if (missing.Count > 0)
        {
            Log("result", "FAILED: expected text not seen: " + string.Join(" | ", missing));
            return 1;
        }
        if (_fileFailures > 0)
        {
            Log("result", $"FAILED: {_fileFailures} resource file(s) could not be downloaded");
            return 1;
        }
        Log("result", _o.Expect.Count > 0 ? $"OK: joined, all {_o.Expect.Count} expected lines seen" : "OK: joined");
        return 0;
    }

    /// <summary>Processes pending messages. Returns true when the connection was closed.</summary>
    private static bool Pump()
    {
        var closed = false;
        _client.MessageReceivedEvent.WaitOne(50);

        NetIncomingMessage msg;
        while ((msg = _client.ReadMessage()) != null)
        {
            try
            {
                switch (msg.MessageType)
                {
                    case NetIncomingMessageType.StatusChanged:
                        closed |= OnStatusChanged(msg);
                        break;
                    case NetIncomingMessageType.Data:
                        OnData(msg);
                        break;
                    case NetIncomingMessageType.ConnectionLatencyUpdated:
                        if (_o.Verbose) Log("net", $"latency {msg.ReadFloat() * 1000:0} ms");
                        break;
                    case NetIncomingMessageType.DiscoveryResponse:
                        msg.ReadByte();
                        var d = ReadPacket<DiscoveryResponse>(msg);
                        Log("discovery", $"{msg.SenderEndPoint}: \"{d.ServerName}\" {d.PlayerCount}/{d.MaxPlayers} players, gamemode {d.Gamemode}, port {d.Port}, password {(d.PasswordProtected ? "yes" : "no")}, LAN {d.LAN}");
                        break;
                    case NetIncomingMessageType.DebugMessage:
                    case NetIncomingMessageType.VerboseDebugMessage:
                    case NetIncomingMessageType.WarningMessage:
                    case NetIncomingMessageType.ErrorMessage:
                        Log("lidgren", msg.ReadString());
                        break;
                }
            }
            catch (Exception ex)
            {
                Log("error", $"while handling {msg.MessageType}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _client.Recycle(msg);
            }
        }

        return closed;
    }

    private static bool OnStatusChanged(NetIncomingMessage msg)
    {
        var status = (NetConnectionStatus)msg.ReadByte();
        var reason = msg.ReadString();

        switch (status)
        {
            case NetConnectionStatus.Connected:
            {
                var hail = msg.SenderConnection.RemoteHailMessage;
                var response = ReadPacket<ConnectionResponse>(hail);
                if (!CompleteHandshake(response)) { _client.Disconnect("server key mismatch"); return true; }
                _myHandle = response.CharacterHandle;
                Log("connected", $"server version {response.ServerVersion}, my player handle {_myHandle}, " +
                                 $"HTTP file server {(response.Settings?.UseHttpServer == true ? "on" : "off")}, mod whitelist entries {response.Settings?.ModWhitelist?.Count ?? 0}");

                var confirm = _client.CreateMessage();
                confirm.Write((byte)PacketType.ConnectionConfirmed);
                confirm.Write(false);
                Send(confirm, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.SyncEvent);
                _confirmedFalseSent = true;
                Log("handshake", "ConnectionConfirmed(false) sent, waiting for the map and client scripts ...");

                if (_o.DownloadFiles != null)
                {
                    if (response.Settings?.UseHttpServer == true) DownloadFilesOverHttp();
                    else Log("files", "the server streams resource files over UDP; they are saved as they arrive");
                }
                return false;
            }
            case NetConnectionStatus.Disconnected:
                Log("disconnected", string.IsNullOrEmpty(reason) ? "(no reason)" : reason);
                return true;
            default:
                if (_o.Verbose) Log("status", status + (string.IsNullOrEmpty(reason) ? "" : " " + reason));
                return false;
        }
    }

    private static void OnData(NetIncomingMessage msg)
    {
        if (_session != null && !msg.Decrypt(_session))
        {
            if (_authFailuresLogged++ < 3) Log("crypto", "dropped a message that failed authentication (replay or not from this session)");
            return;
        }
        var type = (PacketType)msg.ReadByte();

        switch (type)
        {
            case PacketType.FileTransferRequest:
            {
                var start = ReadPacket<DataDownloadStart>(msg);
                _download = new Download
                {
                    Id = start.Id, Type = (FileType)start.FileType, Name = start.FileName, Resource = start.ResourceParent,
                    Hash = start.Md5Hash, Length = start.Length,
                };
                Log("download", $"offered {_download.Type} \"{start.FileName}\" from resource \"{start.ResourceParent}\" ({start.Length} bytes) -> accepting");
                var accept = _client.CreateMessage();
                accept.Write((byte)PacketType.FileAcceptDeny);
                accept.Write(start.Id);
                accept.Write(true);
                Send(accept, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.SyncEvent);
                break;
            }
            case PacketType.FileTransferTick:
            {
                var id = msg.ReadInt32();
                var len = msg.ReadInt32();
                var bytes = msg.ReadBytes(len);
                if (_download != null && _download.Id == id) _download.Data.Write(bytes, 0, bytes.Length);
                break;
            }
            case PacketType.FileTransferComplete:
            {
                var id = msg.ReadInt32();
                if (_download == null || _download.Id != id) break;
                OnDownloadComplete(_download);
                _download = null;
                break;
            }
            case PacketType.ChatData:
            {
                var chat = ReadPacket<ChatData>(msg);
                var line = (string.IsNullOrEmpty(chat.Sender) ? "" : chat.Sender + ": ") + Strip(chat.Message);
                _chatLog.AppendLine(line);
                Log("chat", line);
                break;
            }
            case PacketType.CreateEntity:
            {
                var e = ReadPacket<CreateEntity>(msg);
                var p = e.Properties;
                Log("entity", $"create {(EntityType)e.EntityType} #{e.NetHandle} model {ModelName(p?.ModelHash ?? 0)} at {Fmt(p?.Position)}{Describe(p, "Position", "ModelHash", "EntityType")}");
                break;
            }
            case PacketType.DeleteEntity:
                Log("entity", $"delete #{ReadPacket<DeleteEntity>(msg).NetHandle}");
                break;
            case PacketType.UpdateEntityProperties:
            {
                var u = ReadPacket<UpdateEntity>(msg);
                var delta = u.Properties;
                if (u.NetHandle == _myHandle && delta?.Position != null)
                {
                    _position = delta.Position;
                    Log("me", $"server moved me to {Fmt(_position)}");
                }
                Log("entity", $"update {(EntityType)u.EntityType} #{u.NetHandle}{(u.NetHandle == _myHandle ? " (me)" : "")}{Describe(delta)}");
                break;
            }
            case PacketType.NativeCall:
            case PacketType.NativeOnDisconnect:
            {
                var n = ReadPacket<NativeData>(msg);
                var args = n.Arguments ?? new List<NativeArgument>();
                Log(type == PacketType.NativeCall ? "native" : "native@disconnect",
                    $"{NativeName(n.Hash)}({string.Join(", ", args.Select(ArgToString))}){(n.Id != 0 ? $" -> reply id {n.Id} expected" : "")}");
                ApplyNativeSideEffects(n.Hash, args);
                break;
            }
            case PacketType.NativeTick:
            {
                var n = ReadPacket<NativeTickCall>(msg);
                Log("native@tick", $"{n.Identifier}: {NativeName(n.Native?.Hash ?? 0)}");
                break;
            }
            case PacketType.ServerEvent:
            {
                var e = ReadPacket<SyncEvent>(msg);
                Log("event", $"{(ServerEventType)e.EventType}({string.Join(", ", (e.Arguments ?? new List<NativeArgument>()).Select(ArgToString))})");
                break;
            }
            case PacketType.SyncEvent:
            {
                var e = ReadPacket<SyncEvent>(msg);
                Log("sync-event", $"{(SyncEventType)e.EventType}({string.Join(", ", (e.Arguments ?? new List<NativeArgument>()).Select(ArgToString))})");
                break;
            }
            case PacketType.ScriptEventTrigger:
            {
                var e = ReadPacket<ScriptEventTrigger>(msg);
                Log("client-event", $"{e.Resource}: {e.EventName}({string.Join(", ", (e.Arguments ?? new List<NativeArgument>()).Select(ArgToString))})");
                break;
            }
            case PacketType.RpcResponse:
            {
                var r = ReadPacket<RpcResponse>(msg);
                _rpcNames.TryGetValue(r.Id, out var name);
                var line = r.Ok ? $"rpc {name} ok {r.Payload}" : $"rpc {name} error {r.ErrorCode}: {r.ErrorMessage}";
                _chatLog.AppendLine(line);
                Log("rpc", $"#{r.Id} {line} ({(_clock.Elapsed - (_rpcSent.TryGetValue(r.Id, out var sent) ? sent : _clock.Elapsed)).TotalMilliseconds:0} ms)");
                break;
            }
            case PacketType.RpcRequest:
            {
                // the server calls us (API.callClient): answer with the arguments echoed back
                var r = ReadPacket<RpcRequest>(msg);
                var line = $"rpc-in {r.Name} {r.Payload}";
                _chatLog.AppendLine(line);
                Log("rpc", line);
                var reply = _client.CreateMessage();
                WritePacket(reply, PacketType.RpcResponse, new RpcResponse { Id = r.Id, Ok = true, Payload = r.Payload ?? "null" });
                Send(reply, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.Rpc);
                break;
            }
            case PacketType.PlayerDisconnect:
                Log("player", $"disconnected #{ReadPacket<PlayerDisconnect>(msg).Id}");
                break;
            case PacketType.StopResource:
                Log("resource", $"stop {msg.ReadString()}");
                break;
            case PacketType.RedownloadManifest:
                Log("resource", "server asks to re-download the file manifest (HTTP)");
                break;
            case PacketType.PedPureSync:
            {
                var len = msg.ReadInt32();
                var ped = PacketOptimization.ReadPurePedSync(msg.ReadBytes(len));
                if (_o.Verbose) Log("sync", $"ped #{ped.NetHandle} at {Fmt(ped.Position)} hp {ped.PlayerHealth}");
                break;
            }
            case PacketType.VehiclePureSync:
            {
                var len = msg.ReadInt32();
                var veh = PacketOptimization.ReadPureVehicleSync(msg.ReadBytes(len));
                if (_o.Verbose) Log("sync", $"vehicle #{veh.VehicleHandle} at {Fmt(veh.Position)}");
                break;
            }
            case PacketType.PedLightSync:
            case PacketType.VehicleLightSync:
            case PacketType.BasicSync:
            case PacketType.BulletSync:
            case PacketType.BulletPlayerSync:
            case PacketType.UnoccupiedVehSync:
            case PacketType.BasicUnoccupiedVehSync:
            case PacketType.UnoccupiedVehStartStopSync:
                if (_o.Verbose) Log("sync", $"{type} ({msg.LengthBytes} bytes)");
                break;
            default:
                Log("packet", $"{type} ({msg.LengthBytes} bytes)");
                break;
        }
    }

    private static void OnDownloadComplete(Download d)
    {
        var bytes = d.Data.ToArray();
        switch (d.Type)
        {
            case FileType.Map:
            {
                var map = Serializer.Deserialize<ServerMap>(new MemoryStream(bytes));
                var w = map.World;
                Log("map", $"world time {w?.Hours:00}:{w?.Minutes:00}, weather {w?.Weather}, " +
                           $"{map.Players?.Count ?? 0} players, {map.Vehicles?.Count ?? 0} vehicles, {map.Objects?.Count ?? 0} objects, " +
                           $"{map.Blips?.Count ?? 0} blips, {map.Markers?.Count ?? 0} markers, {map.Pickups?.Count ?? 0} pickups, " +
                           $"{map.TextLabels?.Count ?? 0} labels, {map.Peds?.Count ?? 0} peds");
                if (map.Players != null)
                {
                    foreach (var kv in map.Players)
                        Log("map", $"  player #{kv.Key} \"{kv.Value.Name}\" at {Fmt(kv.Value.Position)}{(kv.Key == _myHandle ? " (me)" : "")}");
                }
                if (map.Vehicles != null)
                {
                    foreach (var kv in map.Vehicles)
                        Log("map", $"  vehicle #{kv.Key} {ModelName(kv.Value.ModelHash)} at {Fmt(kv.Value.Position)}");
                }
                break;
            }
            case FileType.Script:
            {
                var text = Encoding.UTF8.GetString(bytes);
                var firstLine = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
                _resourcesWithScripts.Add(d.Resource);
                Log("script", $"client script \"{d.Name}\" from \"{d.Resource}\" ({bytes.Length} bytes, md5 {d.Hash}): {firstLine}");
                break;
            }
            case FileType.EndOfTransfer:
            {
                var confirm = _client.CreateMessage();
                confirm.Write((byte)PacketType.ConnectionConfirmed);
                confirm.Write(true);
                confirm.Write(_resourcesWithScripts.Count);
                foreach (var r in _resourcesWithScripts) confirm.Write(r);
                Send(confirm, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.SyncEvent);
                _joined = true;
                Log("joined", $"download finished, ConnectionConfirmed(true) sent for [{string.Join(", ", _resourcesWithScripts)}]. I am in the game world now.");
                break;
            }
            case FileType.Normal when _o.DownloadFiles != null:
            {
                // UDP-streamed <file>: same layout as the HTTP path, <dir>/<resource>/<path>.
                if (ResourceFileDownloader.TryGetLocalPath(_o.DownloadFiles, d.Resource, d.Name, out var target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, bytes);
                    Log("files", $"{d.Resource}/{d.Name} ({bytes.Length} bytes) saved to {target}");
                }
                else
                {
                    _fileFailures++;
                    Log("files", $"REFUSED unsafe file path {d.Resource}/{d.Name}");
                }
                break;
            }
            default:
                Log("download", $"{d.Type} \"{d.Name}\" ({bytes.Length} bytes) received");
                break;
        }
    }

    /// <summary>The game client's HTTP path (Client/Main/Network/Download.cs) with the shared downloader.</summary>
    private static void DownloadFilesOverHttp()
    {
        var address = $"http://{_o.Host}:{_o.Port}";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var downloader = new ResourceFileDownloader(address, _o.DownloadFiles, url => http.GetByteArrayAsync(url).GetAwaiter().GetResult())
            {
                Progress = (label, index, total) => Log("files", $"{index}/{total} {label}"),
                Log = text => Log("files", text),
            };
            var result = downloader.Run();
            _fileFailures += result.Failed.Count;
            Log("files", $"{address}: {result}" + (result.Failed.Count > 0 ? " -> " + string.Join(" | ", result.Failed) : ""));
        }
        catch (Exception ex)
        {
            _fileFailures++;
            Log("files", $"FAILED to download the manifest from {address}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static uint _rpcNext;
    private static readonly Dictionary<uint, string> _rpcNames = new();
    private static readonly Dictionary<uint, TimeSpan> _rpcSent = new();

    private static void SendRpc(string name, string json)
    {
        var id = ++_rpcNext;
        _rpcNames[id] = name;
        _rpcSent[id] = _clock.Elapsed;
        var msg = _client.CreateMessage();
        WritePacket(msg, PacketType.RpcRequest, new RpcRequest { Id = id, Name = name, Resource = "bot", Payload = json, TimeoutMs = 5000, Origin = RpcOrigin.Client });
        Send(msg, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.Rpc);
        Log("rpc", $"#{id} call {name}({json})");
    }

    // ---- the encrypted session (T-009) ----
    private static KeyPair _handshakeKey;
    private static byte[] _pin;
    private static NetSessionEncryption _session;
    private static int _authFailuresLogged;

    /// <summary>The approval hail arrived: derive the session key from the server's public key, or note a plaintext session.</summary>
    private static bool CompleteHandshake(ConnectionResponse response)
    {
        var serverKey = response.ServerPublicKey;
        if (serverKey == null || serverKey.Length != 32)
        {
            if (_pin != null) { Log("crypto", "the server offered no session key but --pin was given: leaving"); return false; }
            Log("crypto", _handshakeKey == null ? "plaintext session (asked for none)" : "plaintext session: the server offered no key (old server, or RequireEncryption off)");
            _chatLog.AppendLine("crypto: plaintext session");
            return true;
        }
        if (_handshakeKey == null) { Log("crypto", "the server offered a key but this bot sent none; plaintext session"); return true; }
        var fingerprint = SessionHandshake.Fingerprint(serverKey);
        if (_pin != null && !_pin.AsSpan().SequenceEqual(serverKey))
        {
            Log("crypto", $"server key mismatch: expected {SessionHandshake.Fingerprint(_pin)}, got {fingerprint}; leaving");
            _chatLog.AppendLine("crypto: server key mismatch");
            return false;
        }
        var key = SessionHandshake.DeriveSessionKey(_handshakeKey.PrivateKey, serverKey, _handshakeKey.PublicKey, serverKey);
        _session = new NetSessionEncryption(_client, new SessionCipher(key, isServer: false), fingerprint);
        Log("crypto", $"encrypted session (X25519 + AES-256-GCM), server key {fingerprint}" + (_pin != null ? " (pinned)" : "") + $", session token {(response.SessionToken == null ? "none" : SessionHandshake.ToHex(response.SessionToken))}");
        _chatLog.AppendLine("crypto: encrypted session " + fingerprint);
        return true;
    }

    /// <summary>Every message to the server goes through here: encrypted once the session exists.</summary>
    private static void Send(NetOutgoingMessage msg, NetDeliveryMethod method, int channel)
    {
        if (_session != null) msg.Encrypt(_session);
        _client.SendMessage(msg, method, channel);
    }

    private static void SendChat(string text)
    {
        var msg = _client.CreateMessage();
        WritePacket(msg, PacketType.ChatData, new ChatData { Message = text });
        Send(msg, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.Chat);
        Log("say", text);
    }

    private static void SendPureSync()
    {
        var data = new PedData
        {
            NetHandle = _myHandle,
            Flag = 0,
            Position = _position,
            Quaternion = new Vector3(0f, 0f, _heading),
            Velocity = new Vector3(0f, 0f, 0f),
            PlayerHealth = 100,
            PedArmor = 0,
            Speed = 0,
            WeaponHash = unchecked((int)WeaponHash.Unarmed),
            WeaponAmmo = 0,
            AimCoords = new Vector3(0f, 0f, 0f),
        };
        var bin = PacketOptimization.WritePureSync(data);
        var msg = _client.CreateMessage();
        msg.Write((byte)PacketType.PedPureSync);
        msg.Write(bin.Length);
        msg.Write(bin);
        Send(msg, NetDeliveryMethod.UnreliableSequenced, (int)ConnectionChannel.PureSync);
    }

    /// <summary>A real client would execute the native; the bot honours the ones that move it.</summary>
    private static void ApplyNativeSideEffects(ulong hash, List<NativeArgument> args)
    {
        var name = NativeName(hash);
        if ((name == "SET_ENTITY_COORDS" || name == "SET_ENTITY_COORDS_NO_OFFSET") && args.Count >= 4 && TargetsMe(args[0]))
        {
            var xyz = args.Skip(1).Take(3).Select(ArgToFloat).ToArray();
            if (xyz.All(v => v.HasValue))
            {
                _position = new Vector3(xyz[0].Value, xyz[1].Value, xyz[2].Value);
                Log("me", $"teleported to {Fmt(_position)}");
            }
        }
        else if (name == "SET_ENTITY_HEADING" && args.Count >= 2 && TargetsMe(args[0]))
        {
            _heading = ArgToFloat(args[1]) ?? _heading;
        }
    }

    private static bool TargetsMe(NativeArgument a)
    {
        if (a == null) return false;
        var t = a.GetType().Name;
        if (t.Contains("LocalPlayer") || t.Contains("LocalGamePlayer")) return true;
        var handle = a.GetType().GetProperty("NetHandle")?.GetValue(a);
        return handle is int h && h == _myHandle;
    }

    private static float? ArgToFloat(NativeArgument a)
    {
        var v = ArgValue(a);
        return v switch { float f => f, double d => (float)d, int i => i, _ => null };
    }

    private static object ArgValue(NativeArgument a)
    {
        if (a == null) return null;
        var type = a.GetType();
        var data = type.GetProperty("Data");
        if (data != null) return data.GetValue(a);
        var handle = type.GetProperty("NetHandle");
        if (handle != null) return handle.GetValue(a);
        var x = type.GetProperty("X");
        if (x != null) return new Vector3((float)x.GetValue(a), (float)type.GetProperty("Y").GetValue(a), (float)type.GetProperty("Z").GetValue(a));
        return null;
    }

    private static string ArgToString(NativeArgument a)
    {
        if (a == null) return "null";
        var typeName = a.GetType().Name.Replace("Argument", "");
        var value = ArgValue(a);
        return typeName switch
        {
            "Entity" or "EntityPointer" => $"entity #{value}",
            "LocalPlayer" => "LocalPlayer",
            "LocalGamePlayer" => "LocalGamePlayer",
            "OpponentPedHandle" => $"opponent #{value}",
            "List" => value is System.Collections.IEnumerable list ? "[" + string.Join(", ", list.Cast<NativeArgument>().Select(ArgToString)) + "]" : "[]",
            _ => value == null ? typeName : FmtValue(value),
        };
    }

    private static string Describe(object o, params string[] skip)
    {
        if (o == null) return "";
        var parts = new List<string>();
        foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (skip.Contains(p.Name) || p.GetIndexParameters().Length > 0) continue;
            object v;
            try { v = p.GetValue(o); } catch { continue; }
            if (v == null) continue;
            if (v is System.Collections.ICollection c && c.Count == 0) continue;
            if (v is byte b && b == 0 || v is int i && i == 0 || v is float f && f == 0f || v is bool bo && !bo) continue;
            parts.Add($"{p.Name}={FmtValue(v)}");
        }
        return parts.Count == 0 ? "" : " {" + string.Join(", ", parts) + "}";
    }

    private static string FmtValue(object v) => v switch
    {
        null => "null",
        Vector3 vec => Fmt(vec),
        string s => "\"" + s + "\"",
        bool b => b ? "true" : "false",
        float f => f.ToString("0.##", CultureInfo.InvariantCulture),
        System.Collections.IEnumerable e when v is not string => "[" + string.Join(", ", e.Cast<object>().Select(FmtValue)) + "]",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    private static string Fmt(Vector3 v) => v == null ? "?" : string.Format(CultureInfo.InvariantCulture, "({0:0.0}, {1:0.0}, {2:0.0})", v.X, v.Y, v.Z);

    private static string ModelName(int hash)
    {
        if (Enum.IsDefined(typeof(VehicleHash), hash)) return Enum.GetName(typeof(VehicleHash), hash);
        return "0x" + unchecked((uint)hash).ToString("X8");
    }

    private static string NativeName(ulong hash)
    {
        return Enum.IsDefined(typeof(GTA.Native.Hash), hash) ? Enum.GetName(typeof(GTA.Native.Hash), hash) : "0x" + hash.ToString("X16");
    }

    private static string Strip(string text) => text == null ? "" : ColorCodes.Replace(text, "");

    private static void WritePacket(NetOutgoingMessage msg, PacketType type, object payload)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, payload);
        var bin = stream.ToArray();
        msg.Write((byte)type);
        msg.Write(bin.Length);
        msg.Write(bin);
    }

    private static T ReadPacket<T>(NetIncomingMessage msg)
    {
        var len = msg.ReadInt32();
        var bytes = msg.ReadBytes(len);
        return Serializer.Deserialize<T>(new MemoryStream(bytes));
    }

    private static void Log(string tag, string text)
    {
        Console.WriteLine($"[{_clock.Elapsed.TotalSeconds,6:0.00}] [{tag}] {text}");
    }
}
