using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace GTANetwork.Util
{
    public class LogManager
    {
        public static string LogDirectory = Main.GTANInstallDir + "\\logs";

        public static void CreateLogDirectory()
        {
            if (!Directory.Exists(Main.GTANInstallDir + "\\logs"))
                Directory.CreateDirectory(Main.GTANInstallDir + "\\logs");
        }

        public static void SimpleLog(string filename, string text)
        {
            CreateLogDirectory();
            try
            {
                lock (errorLogLock)
                    File.AppendAllText(LogDirectory + "\\" + filename + ".log", "[" + DateTime.Now.ToString("hh:mm:ss") + "] " + text + "\r\n");
            }
            catch{}
        }
        public static void CefLog(string text)
        {
            CreateLogDirectory();
            try
            {
                lock (errorLogLock)
                    File.AppendAllText(LogDirectory + "\\" + "CEF.log", "[" + DateTime.Now.ToString("hh:mm:ss") + "] " + text + "\r\n");
            }
            catch { }
        }
        public static void CefLog(Exception ex, string source)
        {
            CreateLogDirectory();
            lock (errorLogLock)
            {
                File.AppendAllText(LogDirectory + "\\CEF.log", ">> EXCEPTION OCCURED AT " + DateTime.Now + " FROM " + source + "\r\n" + ex.ToString() + "\r\n\r\n");
            }
        }

        class ThreadInfo
        {
            public string text { get; set; }
        }

        public static void DebugLog(string text)
        {
            //Console.WriteLine(text);
            if (Main.SaveDebugToFile)
            {
                CreateLogDirectory();
                lock (errorLogLock)
                {
                    File.AppendAllText(LogDirectory + "\\Debug.log", "[" + DateTime.Now.ToString("hh:mm:ss") + "] " + text + Environment.NewLine);
                }
            }
            if (Main.PlayerSettings.DebugMode)
            {
                ThreadInfo threadInfo = new ThreadInfo();
                threadInfo.text = text;
                ThreadPool.QueueUserWorkItem(new WaitCallback(Work), threadInfo);
            }
        }

        public static void Work(object a)
        {
            ThreadInfo threadInfo = a as ThreadInfo;
            byte[] bytes = new byte[1024];
            try
            {
                IPEndPoint remoteEP = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 11000);
                using (Socket sender = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    if (!sender.Connected)
                    {
                        sender.Connect(remoteEP);
                    }
                    byte[] msg = Encoding.ASCII.GetBytes(threadInfo.text + "<EOL>");
                    int bytesSent = sender.Send(msg);
                }

            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
        }

        /// <summary>
        /// Debug mode: keeps the diagnostic log lines (API probe, overlay frames, paint and request traces, present
        /// cost) in the code and switches them on per build or per player instead of adding and removing them.
        /// On in Debug builds, with &lt;DebugMode&gt;true&lt;/DebugMode&gt; in settings.xml, or with GTAN_DEBUG=1 in the
        /// environment (the launcher's --debug sets it).
        /// </summary>
        public static bool Verbose;

        /// <summary>Runtime.log line that is only written in debug mode.</summary>
        public static void VerboseLog(string text)
        {
            if (Verbose) RuntimeLog(text);
        }

        /// <summary>CEF.log line that is only written in debug mode.</summary>
        public static void VerboseCefLog(string text)
        {
            if (Verbose) CefLog(text);
        }

        public static void RuntimeLog(string text)
        {
            try
            {
                Debug.WriteLine(text);
                CreateLogDirectory();
                lock (errorLogLock)
                {
                    File.AppendAllText(LogDirectory + "\\Runtime.log", "[" + DateTime.Now.ToString("hh:mm:ss") + "] " + text + "\r\n");
                }
            }
            catch (Exception) { }
        }

        public static object errorLogLock = new object();
        public static void LogException(Exception ex, string source)
        {
            CreateLogDirectory();
            lock (errorLogLock)
            {
                File.AppendAllText(LogDirectory + "\\Error.log", ">> EXCEPTION OCCURED AT " + DateTime.Now + " FROM " + source + "\r\n" + ex.ToString() + "\r\n\r\n");
            }
        }
    }
}