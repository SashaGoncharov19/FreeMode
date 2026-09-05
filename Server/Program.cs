using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using GTANetworkShared;
using GTANetworkServer.Constant;

namespace GTANetworkServer
{
    internal static class Program
    {
        [DllImport("Kernel32.dll")]
        private static extern bool SetConsoleCtrlHandler(EventHandler handler, bool add);
        private delegate bool EventHandler(CtrlType sig);

        private static EventHandler _handler;

        private static object _consolelock = new object();
        private static object _filelock = new object();
        private static bool _log;

        // Wall-clock milliseconds. Entity movement start times are compared against the client's clock, so
        // this one stays as it is; use MonotonicMs() for timeouts and rate limits.
        public static long GetTicks()
        {
            return DateTime.Now.Ticks/10000;
        }

        // Milliseconds from a monotonic clock: immune to NTP steps and time zone changes.
        public static long MonotonicMs()
        {
            return Environment.TickCount64;
        }

        public static void ToFile(string path, string str)
        {
            File.AppendAllText(path, "[" + DateTime.Now.TimeOfDay.ToString(@"hh\:mm\:ss") + "] " + str + Environment.NewLine);
        }

        public static void Output(string str, LogCat category = LogCat.Info)
        {
            lock (_consolelock)
            {
                switch (category)
                {
                    case LogCat.Info:
                        Console.ForegroundColor = ConsoleColor.Gray;
                        break;
                    case LogCat.Warn:
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        break;
                    case LogCat.Error:
                        Console.ForegroundColor = ConsoleColor.Red;
                        break;
                    case LogCat.Debug:
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(category), category, null);
                }
                Console.WriteLine("[" + DateTime.Now.TimeOfDay.ToString(@"hh\:mm\:ss") + "] " + str);
            }

            if (!_log) return;
            lock (_filelock)
            {
                File.AppendAllText("server.log", "[" + DateTime.Now.TimeOfDay.ToString(@"hh\:mm\:ss") + "] " + str + Environment.NewLine);
            }
        }

        public static int GetHash(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return 0;

            var bytes = Encoding.UTF8.GetBytes(input.ToLower().ToCharArray());
            uint hash = 0;

            for (int i = 0, length = bytes.Length; i < length; i++)
            {
                hash += bytes[i];
                hash += (hash << 10);
                hash ^= (hash >> 6);
            }

            hash += (hash << 3);
            hash ^= (hash >> 11);
            hash += (hash << 15);

            return unchecked((int)hash);
        }

        public static string GetHashSHA256(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(bytes);
                return hash.Aggregate(string.Empty, (current, x) => current + $"{x:x2}");
            }
        }


        private static string Location => AppDomain.CurrentDomain.BaseDirectory;

        internal static GameServer ServerInstance { get; set; }
        internal static bool CloseProgram;

        [DllImport("kernel32.dll", EntryPoint = "DeleteFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteFileNative(string name);

        /// <summary>
        /// Deletes a file through the Win32 API. Used to strip the "Zone.Identifier" alternate data stream
        /// (the "downloaded from the internet" mark) from resource assemblies. No-op outside Windows.
        /// </summary>
        public static bool DeleteFile(string name)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
            return DeleteFileNative(name);
        }


        private static void Main()
        {
            _handler += Handler;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                SetConsoleCtrlHandler(_handler, true);
            }
            else
            {
                setupHandlers();
            }

            var settings = ServerSettings.ReadSettings(Location + "settings.xml");
            
            _log = settings.LogToFile;

            if (_log) File.AppendAllText("server.log", "-> SERVER STARTED AT " + DateTime.Now);

            var serverVersion = ParseableVersion.FromAssembly(Assembly.GetExecutingAssembly());

            Console.WriteLine("=======================================================================");
            Console.WriteLine("= GRAND THEFT AUTO NETWORK v{0}", serverVersion);
            Console.WriteLine("=======================================================================");
            Console.WriteLine("= Server Name: " + settings.Name);
            Console.WriteLine("= Server Port: " + settings.Port);
            var serverKey = GTANetworkShared.Crypto.ServerKey.LoadOrCreate(Location + "server.key");
            Console.WriteLine("= Public key: " + serverKey.PublicKeyHex + (serverKey.Created ? "  (server.key created - back it up like a certificate)" : ""));
            Console.WriteLine("= Key fingerprint: " + serverKey.Fingerprint + "; pinned connect string: <host>:" + settings.Port + "#" + serverKey.PublicKeyHex);
            Console.WriteLine("= Encryption: " + (settings.RequireEncryption ? "required (X25519 + AES-256-GCM per session)" : "optional: old clients may join in plaintext"));
            Console.WriteLine("= Server FQDN: " + settings.fqdn);
            Console.WriteLine("=");
            Console.WriteLine("= Player Limit: " + settings.MaxPlayers);
            Console.WriteLine("= Log Level: " + settings.LogLevel + " (1: ERROR, 2: DEBUG, 3: VERBOSE)");
            Console.WriteLine("= Runtime: " + RuntimeInformation.FrameworkDescription + " on " + RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture + ")");
            Console.WriteLine("=======================================================================");

            if (settings.Port != 4499) Output("WARN: Port is not the default one, players on your local network won't be able to automatically detect you!");

            Output("Starting...");

            if (!Directory.Exists("resources"))
            {
                Output("ERROR: Necessary \"resources\" folder does not exist!");
                Console.Read();
                return;
            }

            GTANetworkServer.Crypto.AesGcmNet.Install();
            ServerInstance = new GameServer(settings) {AllowDisplayNames = true, ServerKey = serverKey};

            ServerInstance.Start(settings.Resources.Select(r => r.Path).ToArray());

            Output("Started! Waiting for connections.");

 
            var tickWatch = new System.Diagnostics.Stopwatch();
            while (!CloseProgram)
            {
                tickWatch.Restart();
                ServerInstance.Tick();
                Managers.Metrics.TickDone(tickWatch.Elapsed.TotalMilliseconds);
                Thread.Sleep(1000/60);
            }

        }

        private static int _terminating;

        private static bool Handler(CtrlType sig)
        {
            if (Interlocked.Exchange(ref _terminating, 1) != 0) return true;

            Output("Terminating...");
            if (ServerInstance != null)
            {
                ServerInstance.IsClosing = true;
                while (!ServerInstance.ReadyToClose) { Thread.Sleep(10); }
            }
            CloseProgram = true;
            Console.WriteLine("Terminated.");
            return true;
        }

        private enum CtrlType
        {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT = 1,
            CTRL_CLOSE_EVENT = 2,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT = 6
        }

        // Unix: SIGINT (Ctrl+C), SIGTERM (systemd/docker stop), SIGQUIT and SIGHUP all shut the server down cleanly.
        private static readonly List<PosixSignalRegistration> _signalRegistrations = new List<PosixSignalRegistration>();

        private static void setupHandlers()
        {
            foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGQUIT, PosixSignal.SIGHUP })
            {
                try
                {
                    _signalRegistrations.Add(PosixSignalRegistration.Create(signal, context =>
                    {
                        context.Cancel = true; // we exit through the main loop instead of being killed mid-tick
                        new Thread(() => Handler(CtrlType.CTRL_C_EVENT)) { IsBackground = true, Name = "GTAN shutdown" }.Start();
                    }));
                }
                catch (Exception ex)
                {
                    Output("Could not register handler for " + signal + ": " + ex.Message, LogCat.Warn);
                }
            }
        }
    }
}
