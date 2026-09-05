using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace GTANetworkShared.Cef
{
    /// <summary>
    /// The wire between the in-game client and GTANetwork.CefHost.exe, the process that runs Chromium (CefSharp) on
    /// the game's behalf. Chromium cannot live inside GTA5.exe: ScriptHookVDotNet runs the client in a second
    /// AppDomain, and CefSharp's C++/CLI callbacks from Chromium's own threads land in the default AppDomain, where
    /// nothing of ours is loaded (see docs/CEF-UPGRADE.md). So the host is a plain process: commands go down its
    /// stdin, events come back up its stdout (length-prefixed JSON, <see cref="CefHostChannel"/>), and the pixels of
    /// every browser travel through a shared-memory <see cref="CefFrameBuffer"/> the host names in a "frame" event.
    /// </summary>
    public static class CefHostProtocol
    {
        public const int Version = 2;

        // ---- commands, game -> host ----
        /// <summary>Create a browser: Id, W, H, Local (only https://&lt;resource&gt;/ files), Fps, Shared (frames as
        /// D3D11 shared textures, needs the GPU; "texture" events instead of "frame" events when Chromium delivers them).</summary>
        public const string Create = "create";
        /// <summary>Load Url in browser Id (queued by the host until the browser exists).</summary>
        public const string Load = "load";
        /// <summary>Load Html (a page given as text) in browser Id.</summary>
        public const string LoadHtml = "loadHtml";
        /// <summary>Run Code in the main frame of browser Id (no result).</summary>
        public const string Eval = "eval";
        public const string Back = "back";
        public const string Close = "close";
        /// <summary>Resize browser Id to W x H; a new frame buffer follows in a "frame" event.</summary>
        public const string Resize = "resize";
        /// <summary>Focus browser Id: On.</summary>
        public const string Focus = "focus";
        /// <summary>Paints per second for browser Id: Fps (1-60).</summary>
        public const string FrameRate = "fps";
        /// <summary>Mouse move at X, Y (browser coordinates) with Mods; On = the mouse left the browser.</summary>
        public const string MouseMove = "mouseMove";
        /// <summary>Mouse button Button (0 left, 1 middle, 2 right) at X, Y; On = released; Clicks; Mods.</summary>
        public const string MouseClick = "mouseClick";
        /// <summary>Mouse wheel at X, Y by Dx, Dy with Mods.</summary>
        public const string MouseWheel = "mouseWheel";
        /// <summary>Key event: KeyType (0 rawKeyDown, 1 keyDown, 2 keyUp, 3 char), KeyCode (Windows VK / char), NativeKeyCode, Mods.</summary>
        public const string Key = "key";
        /// <summary>Shut Chromium down and exit.</summary>
        public const string Shutdown = "shutdown";

        // ---- events, host -> game ----
        /// <summary>Chromium is up: Chromium, CefVersion, CefSharp versions; Text = how it runs.</summary>
        public const string Ready = "ready";
        /// <summary>Chromium did not start: Text.</summary>
        public const string InitFailed = "initFailed";
        /// <summary>Browser Id exists (input and navigation work).</summary>
        public const string Created = "created";
        /// <summary>Browser Id paints into shared memory FrameName (W x H, Stride bytes per row, generation Gen).</summary>
        public const string Frame = "frame";
        /// <summary>Browser Id renders into a ring of D3D11 textures the host owns: Handles (NT handles duplicated into the
        /// game process; open each once with OpenSharedResource1, close them when the next "textures" replaces them), W x H,
        /// generation Gen. Without Handles, Text says why the host cannot relay textures: use CPU frames for this browser.</summary>
        public const string Textures = "textures";
        /// <summary>Browser Id painted a new frame: it is in the ring texture behind Handle (one of the last "textures"),
        /// complete on the GPU; W x H, dirty rectangle X, Y, Dx, Dy (width, height), Gen = the ring slot.</summary>
        public const string Texture = "texture";
        public const string LoadStart = "loadStart";
        /// <summary>Main frame of browser Id finished loading Url with HTTP Status.</summary>
        public const string LoadEnd = "loadEnd";
        /// <summary>Load failed: Status = error code, Text, Url.</summary>
        public const string LoadError = "loadError";
        /// <summary>Browser Id IsLoading changed.</summary>
        public const string Loading = "loading";
        /// <summary>Page console output: Level (CEF LogSeverity), Text, Source, Line.</summary>
        public const string Console = "console";
        /// <summary>The page called resourceCall(Name, Args...) or resourceEval(Code).</summary>
        public const string JsMessage = "jsMessage";
        /// <summary>The page called gtan.rpc.call(Name, args): Rpc = the request id in the page, Text = the arguments as JSON, Timeout ms.
        /// The answer goes back as an Eval of gtan.rpc._settle(id, ok, json, code, message).</summary>
        public const string Rpc = "rpc";
        /// <summary>The render process of browser Id died: Status, Text.</summary>
        public const string RenderTerminated = "renderTerminated";
        /// <summary>A line for the game's CEF.log: Text.</summary>
        public const string Log = "log";
        /// <summary>Browser Id is gone.</summary>
        public const string Closed = "closed";

        /// <summary>Name of the shared-memory frame buffer of one browser generation.</summary>
        public static string FrameBufferName(int hostPid, int browserId, int generation)
        {
            return "GTANCef_" + hostPid + "_" + browserId + "_" + generation;
        }
    }

    /// <summary>One command or event. Flat on purpose: fields that are zero, false or null are left out on the wire.</summary>
    public sealed class CefHostMessage
    {
        [JsonProperty("t")] public string Type;
        public int Id;
        public int W;
        public int H;
        public int Stride;
        public int Gen;
        public string FrameName;
        public string Url;
        public string Html;
        public string Code;
        public string Name;
        public string Text;
        public string Source;
        public bool Local;
        public bool Shared;
        public bool On;
        public bool IsLoading;
        public int X;
        public int Y;
        public int Dx;
        public int Dy;
        public int Button;
        public int Clicks;
        public int Mods;
        public int KeyType;
        public int KeyCode;
        public int NativeKeyCode;
        public int Fps;
        public int Status;
        public int Level;
        public int Line;
        public object[] Args;
        /// <summary>RPC request id of a page ("rpc" messages).</summary>
        public int Rpc;
        /// <summary>Timeout in ms the page asked for ("rpc" messages).</summary>
        public int Timeout;
        public long Handle;
        public long[] Handles;
        public string Chromium;
        public string CefVersion;
        public string CefSharp;

        public CefHostMessage() { }

        public CefHostMessage(string type, int id = 0)
        {
            Type = type;
            Id = id;
        }

        public override string ToString()
        {
            return Type + (Id != 0 ? " #" + Id : "") + (Url != null ? " " + Url : "") + (Text != null ? " " + Text : "");
        }
    }

    /// <summary>
    /// Length-prefixed JSON messages over a pair of streams (the host's stdin/stdout). Writes are serialised so any
    /// thread may send; reads belong to one reader thread.
    /// </summary>
    public sealed class CefHostChannel : IDisposable
    {
        private const int MaxMessageBytes = 64 * 1024 * 1024;

        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore,
            Formatting = Formatting.None,
        };

        private readonly Stream _input;
        private readonly Stream _output;
        private readonly object _writeLock = new object();
        private readonly byte[] _lengthBuffer = new byte[4];

        /// <param name="input">Where messages of the other side arrive.</param>
        /// <param name="output">Where our messages go.</param>
        public CefHostChannel(Stream input, Stream output)
        {
            _input = input;
            _output = output;
        }

        public void Send(CefHostMessage message)
        {
            var payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message, Json));
            var length = BitConverter.GetBytes(payload.Length);
            lock (_writeLock)
            {
                _output.Write(length, 0, 4);
                _output.Write(payload, 0, payload.Length);
                _output.Flush();
            }
        }

        /// <summary>The next message, or null when the other side closed the stream.</summary>
        public CefHostMessage Receive()
        {
            if (!ReadExactly(_lengthBuffer, 4)) return null;
            var length = BitConverter.ToInt32(_lengthBuffer, 0);
            if (length <= 0 || length > MaxMessageBytes) throw new IOException("CEF host channel: bad message length " + length);

            var payload = new byte[length];
            if (!ReadExactly(payload, length)) return null;
            return JsonConvert.DeserializeObject<CefHostMessage>(Encoding.UTF8.GetString(payload), Json);
        }

        private bool ReadExactly(byte[] buffer, int count)
        {
            var read = 0;
            while (read < count)
            {
                var n = _input.Read(buffer, read, count - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        public void Dispose()
        {
            try { _input.Dispose(); } catch { }
            try { _output.Dispose(); } catch { }
        }
    }
}
