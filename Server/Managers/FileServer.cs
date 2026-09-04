using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using GTANetworkServer.Constant;
using GTANetworkShared;
using Newtonsoft.Json;

namespace GTANetworkServer.Managers
{
    /// <summary>
    /// Serves resource files over HTTP (same URL layout the client expects, previously hosted with Nancy/OWIN):
    ///   GET /manifest.json        -> { "exportedFiles": { "&lt;resource&gt;": [ { path, hash, type } ] } }
    ///   GET /&lt;resource&gt;/&lt;path&gt;  -> the file, but only when the resource declares it in meta.xml
    /// </summary>
    public class FileServer : IDisposable
    {
        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _stopping;

        public void Start(int port)
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://*:" + port + "/");
                _listener.Start();
            }
            catch (Exception ex)
            {
                Program.Output("File server error: " + ex.Message, LogCat.Error);
                Program.Output("Reverting to UDP file server.");
                Program.ServerInstance.UseHTTPFileServer = false;
                _listener = null;
                return;
            }

            _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "GTAN HTTP file server" };
            _thread.Start();

            Program.Output("File server listening on http://*:" + port + "/");
        }

        private void AcceptLoop()
        {
            while (!_stopping)
            {
                HttpListenerContext context;

                try
                {
                    context = _listener.GetContext();
                }
                catch (Exception)
                {
                    if (_stopping) return;
                    Thread.Sleep(50);
                    continue;
                }

                ThreadPool.QueueUserWorkItem(_ => Handle(context));
            }
        }

        private static void Handle(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;
                var path = request.Url?.AbsolutePath ?? "/";

                if (request.HttpMethod != "GET" && request.HttpMethod != "HEAD")
                {
                    response.StatusCode = 405;
                    response.Close();
                    return;
                }

                if (string.Equals(path, "/manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    Dictionary<string, List<FileDeclaration>> snapshot;
                    lock (FileModule.ExportedFiles)
                    {
                        snapshot = FileModule.ExportedFiles.ToDictionary(kv => kv.Key, kv => new List<FileDeclaration>(kv.Value));
                    }

                    var json = JsonConvert.SerializeObject(new FileManifest { exportedFiles = snapshot });
                    Write(response, Encoding.UTF8.GetBytes(json), "application/json", request.HttpMethod == "HEAD");
                    return;
                }

                var file = FileModule.Resolve(path);
                if (file == null)
                {
                    response.StatusCode = 404;
                    response.Close();
                    return;
                }

                var bytes = File.ReadAllBytes(file);
                Write(response, bytes, MimeType.GetMimeType(Path.GetExtension(file)), request.HttpMethod == "HEAD");
            }
            catch (Exception ex)
            {
                Program.Output("File server request failed: " + ex.Message, LogCat.Warn);
                try { context.Response.Abort(); } catch { /* ignored */ }
            }
        }

        private static void Write(HttpListenerResponse response, byte[] body, string contentType, bool headOnly)
        {
            response.StatusCode = 200;
            response.ContentType = contentType ?? "application/octet-stream";
            response.ContentLength64 = body.Length;

            if (!headOnly)
            {
                using (var output = response.OutputStream)
                {
                    output.Write(body, 0, body.Length);
                }
            }

            response.Close();
        }

        public void Dispose()
        {
            _stopping = true;

            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch
            {
                // ignored
            }
        }
    }

    public static class FileModule
    {
        /// <summary>Files (path + hash) every running resource exposes to clients, keyed by resource name.</summary>
        public static Dictionary<string, List<FileDeclaration>> ExportedFiles = new Dictionary<string, List<FileDeclaration>>();

        /// <summary>Maps "/{resource}/{path}" to a file on disk, only when the resource exports that path.</summary>
        internal static string Resolve(string urlPath)
        {
            var decoded = Uri.UnescapeDataString(urlPath ?? string.Empty).TrimStart('/');
            var slash = decoded.IndexOf('/');
            if (slash <= 0 || slash == decoded.Length - 1) return null;

            var resource = decoded.Substring(0, slash);
            var relative = decoded.Substring(slash + 1).Replace('\\', '/');

            List<FileDeclaration> files;
            lock (ExportedFiles)
            {
                if (!ExportedFiles.TryGetValue(resource, out files)) return null;
                files = new List<FileDeclaration>(files);
            }

            if (!files.Any(f => f.path != null && f.path.Replace('\\', '/') == relative)) return null;

            var root = Path.GetFullPath(Path.Combine("resources", resource));
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null; // path traversal

            return File.Exists(full) ? full : null;
        }
    }
}
