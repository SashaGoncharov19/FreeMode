using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace GTANetworkShared
{
    /// <summary>
    /// Fetches the files that running resources export to players (<c>&lt;file src="..."/&gt;</c> in meta.xml) from the
    /// server's HTTP file server: <c>GET /manifest.json</c> lists them per resource, <c>GET /&lt;resource&gt;/&lt;path&gt;</c>
    /// serves each one. Files are stored as <c>&lt;downloadRoot&gt;/&lt;resource&gt;/&lt;path&gt;</c>, which is where the
    /// in-game browser resolves <c>https://&lt;resource&gt;/&lt;path&gt;</c>. A file whose MD5 already matches the manifest
    /// is not fetched again. The game client and the headless bot share this class, so the CI test runs the same code
    /// as the game.
    /// </summary>
    public class ResourceFileDownloader
    {
        public sealed class Result
        {
            public int Downloaded;
            public int UpToDate;
            public int Rejected;
            public readonly List<string> Failed = new List<string>();

            public override string ToString()
            {
                return Downloaded + " downloaded, " + UpToDate + " up to date, " + Rejected + " rejected, " + Failed.Count + " failed";
            }
        }

        private readonly string _address;
        private readonly string _root;
        private readonly Func<string, byte[]> _fetch;

        /// <param name="address">Base address of the file server, e.g. <c>http://1.2.3.4:4499</c>.</param>
        /// <param name="downloadRoot">The local resources folder; every file goes to <c>root/resource/path</c>.</param>
        /// <param name="fetch">Performs a GET and returns the body; must throw when the request fails.</param>
        public ResourceFileDownloader(string address, string downloadRoot, Func<string, byte[]> fetch)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("address");
            if (string.IsNullOrWhiteSpace(downloadRoot)) throw new ArgumentException("downloadRoot");
            _address = address.TrimEnd('/');
            _root = downloadRoot;
            _fetch = fetch ?? throw new ArgumentNullException("fetch");
        }

        /// <summary>Decides whether a downloaded file may be stored (content, target path). Null accepts everything.</summary>
        public Func<byte[], string, bool> Accept { get; set; }

        /// <summary>Called before each file with "resource/path", its index (1-based) and the total count.</summary>
        public Action<string, int, int> Progress { get; set; }

        /// <summary>Checked between files; returning true stops the download.</summary>
        public Func<bool> Cancelled { get; set; }

        public Action<string> Log { get; set; }

        /// <summary>Downloads the manifest and every non-script file it lists. Throws only when the manifest itself
        /// cannot be fetched or parsed; per-file problems end up in <see cref="Result.Failed"/>.</summary>
        public Result Run()
        {
            var result = new Result();

            var manifestJson = Encoding.UTF8.GetString(_fetch(_address + "/manifest.json"));
            var manifest = JsonConvert.DeserializeObject<FileManifest>(manifestJson);
            if (manifest?.exportedFiles == null) throw new InvalidDataException("manifest.json does not contain exportedFiles");

            var files = new List<KeyValuePair<string, FileDeclaration>>();
            foreach (var resource in manifest.exportedFiles)
            {
                if (resource.Value == null) continue;
                foreach (var file in resource.Value)
                {
                    // Client scripts travel over UDP together with the map; only <file> entries come from here.
                    if (file == null || file.type == FileType.Script) continue;
                    files.Add(new KeyValuePair<string, FileDeclaration>(resource.Key, file));
                }
            }

            for (var i = 0; i < files.Count; i++)
            {
                if (Cancelled != null && Cancelled()) break;

                var resource = files[i].Key;
                var file = files[i].Value;
                var label = resource + "/" + file.path;
                Progress?.Invoke(label, i + 1, files.Count);

                string target;
                if (!TryGetLocalPath(_root, resource, file.path, out target))
                {
                    result.Failed.Add(label + ": unsafe path");
                    Log?.Invoke("refusing a resource file with an unsafe path: " + label);
                    continue;
                }

                if (File.Exists(target) && !string.IsNullOrEmpty(file.hash) &&
                    string.Equals(Md5Hex(File.ReadAllBytes(target)), file.hash, StringComparison.OrdinalIgnoreCase))
                {
                    result.UpToDate++;
                    continue;
                }

                byte[] content;
                try
                {
                    content = _fetch(BuildFileUrl(_address, resource, file.path));
                }
                catch (Exception ex)
                {
                    result.Failed.Add(label + ": " + ex.Message);
                    Log?.Invoke("download of " + label + " failed: " + ex.Message);
                    continue;
                }

                if (Accept != null && !Accept(content, target))
                {
                    result.Rejected++;
                    Log?.Invoke("rejected " + label + ": file type not allowed");
                    TryDelete(target);
                    continue;
                }

                if (!string.IsNullOrEmpty(file.hash) && !string.Equals(Md5Hex(content), file.hash, StringComparison.OrdinalIgnoreCase))
                    Log?.Invoke("warning: " + label + " does not match the hash in the manifest (changed on the server after the resource started?)");

                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllBytes(target, content);
                result.Downloaded++;
            }

            return result;
        }

        /// <summary>
        /// Maps a resource name and a relative path from the server to a file below <paramref name="downloadRoot"/>.
        /// Returns false for anything that would escape <c>root/resource/</c> (".." segments, rooted paths, drive
        /// letters) or that is not a valid path at all. Both the HTTP and the UDP file transfer go through this.
        /// </summary>
        public static bool TryGetLocalPath(string downloadRoot, string resource, string relativePath, out string fullPath)
        {
            fullPath = null;

            if (string.IsNullOrWhiteSpace(downloadRoot) || string.IsNullOrWhiteSpace(resource) || string.IsNullOrWhiteSpace(relativePath)) return false;
            if (resource.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || resource == "." || resource == "..") return false;
            if (relativePath.IndexOf(':') >= 0) return false;

            try
            {
                var root = Path.GetFullPath(Path.Combine(downloadRoot, resource));
                root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

                var relative = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)
                    .TrimStart(Path.DirectorySeparatorChar);
                var full = Path.GetFullPath(Path.Combine(root, relative));

                if (full.Length <= root.Length || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;

                fullPath = full;
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }

        /// <summary>URL of one resource file on the file server, every path segment escaped.</summary>
        public static string BuildFileUrl(string address, string resource, string relativePath)
        {
            var segments = relativePath.Replace('\\', '/')
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString);

            return address.TrimEnd('/') + "/" + Uri.EscapeDataString(resource) + "/" + string.Join("/", segments);
        }

        public static string Md5Hex(byte[] content)
        {
            using (var md5 = MD5.Create())
            {
                return BitConverter.ToString(md5.ComputeHash(content)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        public static string Md5Hex(string path)
        {
            using (var md5 = MD5.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // ignored
            }
        }
    }
}
