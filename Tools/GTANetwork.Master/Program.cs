using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GTANetwork.Master;
using GTANetworkShared;
using Lidgren.Network;
using Microsoft.AspNetCore.Http.Json;

// The master list (T-011). Configuration through the environment: MASTER_DATA (folder for master.db, verified.txt,
// welcome.json; default ./data), ASPNETCORE_URLS (default http://0.0.0.0:8080), MASTER_PING=0 to list servers without the
// UDP discovery check (tests behind NAT), MASTER_TTL_SECONDS (default 180: a server that stopped announcing is dropped).

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<JsonOptions>(o => { o.SerializerOptions.PropertyNamingPolicy = null; o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never; });
var app = builder.Build();

var dataDir = Environment.GetEnvironmentVariable("MASTER_DATA") ?? Path.Combine(Directory.GetCurrentDirectory(), "data");
var ping = Environment.GetEnvironmentVariable("MASTER_PING") != "0";
var ttl = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("MASTER_TTL_SECONDS"), out var ttlSeconds) ? ttlSeconds : 180);
var db = new Db(dataDir);
var verified = new VerifiedList(Path.Combine(dataDir, "verified.txt"));
var pinger = new Pinger();
var announceStamps = new Dictionary<string, DateTimeOffset>();
var startedAt = DateTimeOffset.UtcNow;
app.Logger.LogInformation("GTA Network master list: data {Data}, ping {Ping}, ttl {Ttl}s", dataDir, ping ? "on" : "off", (int)ttl.TotalSeconds);

// ---- announces (the 2016 path was POST /addserver with the same JSON) ----
async Task<IResult> Announce(HttpContext http)
{
    MasterServerAnnounce? body;
    try { body = await JsonSerializer.DeserializeAsync<MasterServerAnnounce>(http.Request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
    catch (JsonException ex) { return Results.BadRequest(new { ok = false, reason = "bad json: " + ex.Message }); }
    if (body == null || body.Port <= 0 || body.Port > 65535) return Results.BadRequest(new { ok = false, reason = "port missing" });

    var remote = http.Connection.RemoteIpAddress;
    if (remote != null && remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
    var host = !string.IsNullOrWhiteSpace(body.fqdn) && Regex.IsMatch(body.fqdn.Trim(), "^[A-Za-z0-9.-]{1,253}$") ? body.fqdn.Trim() : remote?.ToString();
    if (string.IsNullOrEmpty(host)) return Results.BadRequest(new { ok = false, reason = "no address" });
    var address = host + ":" + body.Port;

    var token = (body.Token ?? "").Trim();
    if (token.Length < 8) return Results.BadRequest(new { ok = false, reason = "a token of at least 8 characters is required (the server makes one: master.token)" });
    var known = db.TokenOf(address);
    if (known != null && known != token) return Results.Json(new { ok = false, reason = "another token announced this address first" }, statusCode: 403);

    lock (announceStamps)
    {
        if (announceStamps.TryGetValue(address, out var last) && DateTimeOffset.UtcNow - last < TimeSpan.FromSeconds(10))
            return Results.Json(new { ok = false, reason = "announce at most every 10 s" }, statusCode: 429);
        announceStamps[address] = DateTimeOffset.UtcNow;
    }

    var now = DateTimeOffset.UtcNow;
    db.Upsert(new ServerRow(address, Clean(body.ServerName, "GTA Network server", 64), Math.Max(0, body.CurrentPlayers), Math.Max(0, body.MaxPlayers),
        Clean(body.Gamemode, "freeroam", 64), Clean(body.Map, "", 64), body.Passworded, Clean(body.ServerVersion, "", 32), Clean(body.PublicKey, "", 64),
        verified.Contains(address), now, null, token));

    var listed = !ping;
    if (ping)
    {
        // the announced port must answer a discovery request: no listing for fakes and for servers behind a closed port
        var ok = await pinger.PingAsync(host, body.Port, TimeSpan.FromSeconds(3));
        db.SetPingResult(address, ok, now);
        listed = ok;
        if (!ok) app.Logger.LogInformation("{Address} announced but did not answer a discovery request", address);
    }
    else db.SetPingResult(address, true, now);
    return Results.Ok(new { ok = true, listed, address, reason = listed ? null : "the announced port did not answer a discovery request; check the firewall" });
}
app.MapPost("/addserver", (Delegate)Announce);
app.MapPost("/servers/announce", (Delegate)Announce);

// ---- lists ----
List<ServerRow> Live() => db.All(verified.Current).Where(r => r.LastPingOk != null && DateTimeOffset.UtcNow - r.LastSeen < ttl).ToList();

app.MapGet("/servers", () => Results.Ok(new { list = Live().Select(r => r.Address).ToList() }));
app.MapGet("/verified", () => Results.Ok(new { list = Live().Where(r => r.Verified).Select(r => r.Address).ToList() }));
app.MapGet("/servers/full", () => Results.Ok(Live().Select(r => new
{
    address = r.Address, name = r.Name, players = r.Players, maxPlayers = r.MaxPlayers, gamemode = r.Gamemode, map = r.Map,
    passworded = r.Passworded, version = r.Version, publicKey = r.PublicKey, verified = r.Verified, lastSeen = r.LastSeen,
}).ToList()));
app.MapGet("/stats", () => { var live = Live(); return Results.Ok(new { TotalPlayers = live.Sum(r => r.Players), TotalServers = live.Count }); });
app.MapGet("/welcome.json", () =>
{
    var file = Path.Combine(dataDir, "welcome.json");
    return File.Exists(file) ? Results.Content(File.ReadAllText(file), "application/json") : Results.Ok(new { Title = "GTA Network", Message = "Pick a server on the right.", Picture = "" });
});
app.MapGet("/health", () => Results.Ok(new { ok = true, uptimeSeconds = (int)(DateTimeOffset.UtcNow - startedAt).TotalSeconds, servers = Live().Count }));
app.MapGet("/", () => Results.Text("GTA Network master list: GET /servers, /servers/full, /verified, /stats, /welcome.json, /health; POST /addserver", "text/plain"));

// ---- housekeeping: forget servers that stopped announcing, reload the verified list ----
var housekeeping = new PeriodicTimer(TimeSpan.FromSeconds(30));
_ = Task.Run(async () =>
{
    while (await housekeeping.WaitForNextTickAsync())
    {
        try
        {
            var dropped = db.Prune(DateTimeOffset.UtcNow - ttl * 4);
            if (dropped > 0) app.Logger.LogInformation("{Count} server(s) forgotten (no announce for {Ttl}s)", dropped, (int)(ttl * 4).TotalSeconds);
            verified.Reload();
        }
        catch (Exception ex) { app.Logger.LogWarning(ex, "housekeeping failed"); }
    }
});

app.Run();

static string Clean(string? value, string fallback, int max)
{
    if (string.IsNullOrWhiteSpace(value)) return fallback;
    var text = Regex.Replace(value, @"[\p{C}~]", "").Trim();
    return text.Length > max ? text.Substring(0, max) : text;
}

/// <summary>Addresses the operator vouches for: one "host:port" per line in verified.txt.</summary>
sealed class VerifiedList
{
    private readonly string _path;
    private HashSet<string> _set = new(StringComparer.OrdinalIgnoreCase);
    public VerifiedList(string path) { _path = path; Reload(); }
    public ISet<string> Current => _set;
    public bool Contains(string address) => _set.Contains(address);
    public void Reload()
    {
        try
        {
            _set = File.Exists(_path)
                ? new HashSet<string>(File.ReadAllLines(_path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
    }
}

/// <summary>Asks a game server whether it is there: a Lidgren discovery request to its UDP port, as the game's server browser does.</summary>
sealed class Pinger
{
    private readonly SemaphoreSlim _one = new(1, 1);

    public async Task<bool> PingAsync(string host, int port, TimeSpan timeout)
    {
        await _one.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                var config = new NetPeerConfiguration("GTANETWORK") { AutoFlushSendQueue = true };
                config.EnableMessageType(NetIncomingMessageType.DiscoveryResponse);
                var client = new NetClient(config);
                try
                {
                    client.Start();
                    IPAddress? ip;
                    if (!IPAddress.TryParse(host, out ip))
                    {
                        try { ip = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork); }
                        catch { ip = null; }
                    }
                    if (ip == null) return false;
                    client.DiscoverKnownPeer(new IPEndPoint(ip, port));
                    var deadline = DateTime.UtcNow + timeout;
                    while (DateTime.UtcNow < deadline)
                    {
                        client.MessageReceivedEvent.WaitOne(100);
                        NetIncomingMessage? msg;
                        while ((msg = client.ReadMessage()) != null)
                        {
                            if (msg.MessageType == NetIncomingMessageType.DiscoveryResponse) return true;
                            client.Recycle(msg);
                        }
                    }
                    return false;
                }
                finally { client.Shutdown("done"); }
            });
        }
        finally { _one.Release(); }
    }
}
