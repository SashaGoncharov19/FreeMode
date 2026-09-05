using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using GTANetworkShared;

namespace GTANetwork.Launcher;

public enum DlcPackState { Missing, Ready, Corrupt }

/// <summary>
/// The launcher's side of custom DLC packs (T-014, D-10): fetch a server's list (GET /dlcpacks.json), download each pack into
/// &lt;install&gt;/dlcpacks/&lt;name&gt;/dlc.rpf and verify its SHA-256 and size. Applying the packs to the game (the overlay
/// at game start) is the step behind Q-15 and is not here yet; <c>mounted.json</c> in the same folder is what the client reports.
/// </summary>
public static class DlcPacks
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static string PackDir(Paths paths, string name) => Path.Combine(paths.DlcPacksDir, name);
    public static string PackFile(Paths paths, string name) => Path.Combine(PackDir(paths, name), "dlc.rpf");

    /// <summary>GET http://host:port/dlcpacks.json — the packs the server declares (empty when it declares none).</summary>
    public static async Task<List<DlcPackInfo>> FetchAsync(string host, int port, CancellationToken token = default)
    {
        var url = $"http://{host}:{port}/dlcpacks.json";
        using var response = await Http.GetAsync(url, token);
        if (!response.IsSuccessStatusCode) throw new LauncherException($"{url} answered {(int)response.StatusCode}: the server has no HTTP file server (<httpserver>false</httpserver>) or is not a GTA Network server");
        var json = await response.Content.ReadAsStringAsync(token);
        var list = JsonSerializer.Deserialize<List<DlcPackInfo>>(json, JsonOptions) ?? new List<DlcPackInfo>();
        foreach (var pack in list)
        {
            if (!DlcPackNames.IsValid(pack.name)) throw new LauncherException($"the server declares a DLC pack with an invalid name \"{pack.name}\"");
        }
        return list;
    }

    /// <summary>What we have for this pack: nothing, the right file, or a file whose hash or size differs.</summary>
    public static DlcPackState StateOf(Paths paths, DlcPackInfo pack)
    {
        var file = PackFile(paths, pack.name);
        if (!File.Exists(file)) return DlcPackState.Missing;
        var info = new FileInfo(file);
        if (pack.size > 0 && info.Length != pack.size) return DlcPackState.Corrupt;
        return string.Equals(Sha256Of(file), pack.sha256, StringComparison.OrdinalIgnoreCase) ? DlcPackState.Ready : DlcPackState.Corrupt;
    }

    /// <summary>Downloads one pack to a temporary file, verifies it and moves it into place. Throws on a hash or size mismatch.</summary>
    public static async Task DownloadAsync(Paths paths, DlcPackInfo pack, Action<long, long>? progress = null, CancellationToken token = default)
    {
        if (!DlcPackNames.IsValid(pack.name)) throw new LauncherException($"invalid DLC pack name \"{pack.name}\"");
        if (string.IsNullOrWhiteSpace(pack.url) || !Uri.TryCreate(pack.url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new LauncherException($"DLC pack \"{pack.name}\": the url must be http(s), got \"{pack.url}\"");
        Directory.CreateDirectory(PackDir(paths, pack.name));
        var target = PackFile(paths, pack.name);
        var temp = target + ".part";
        try
        {
            using (var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token))
            {
                if (!response.IsSuccessStatusCode) throw new LauncherException($"DLC pack \"{pack.name}\": {pack.url} answered {(int)response.StatusCode}");
                var total = response.Content.Headers.ContentLength ?? pack.size;
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    done += read;
                    if (pack.size > 0 && done > pack.size) throw new LauncherException($"DLC pack \"{pack.name}\": more than the declared {pack.size} bytes arrived");
                    progress?.Invoke(done, total);
                }
            }
            var length = new FileInfo(temp).Length;
            if (pack.size > 0 && length != pack.size) throw new LauncherException($"DLC pack \"{pack.name}\": {length} bytes downloaded, {pack.size} declared");
            var hash = Sha256Of(temp);
            if (!string.Equals(hash, pack.sha256, StringComparison.OrdinalIgnoreCase)) throw new LauncherException($"DLC pack \"{pack.name}\": sha256 mismatch (got {hash}, the server declares {pack.sha256}); the file was not kept");
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignored */ }
        }
    }

    public sealed record PrepareResult(List<DlcPackInfo> Packs, List<string> Downloaded, List<string> UpToDate, List<(string Name, string Error)> Failed)
    {
        public bool Ok => Failed.Count == 0;
    }

    /// <summary>Fetches the server's list and downloads what is missing or corrupt. Never throws for one pack's failure: it is reported.</summary>
    public static async Task<PrepareResult> PrepareAsync(Paths paths, string host, int port, Action<string>? log = null, CancellationToken token = default)
    {
        var packs = await FetchAsync(host, port, token);
        var downloaded = new List<string>(); var upToDate = new List<string>(); var failed = new List<(string, string)>();
        log?.Invoke(packs.Count == 0 ? $"{host}:{port} declares no DLC packs." : $"{host}:{port} declares {packs.Count} DLC pack(s).");
        foreach (var pack in packs)
        {
            var state = StateOf(paths, pack);
            if (state == DlcPackState.Ready) { upToDate.Add(pack.name); log?.Invoke($"  {pack.name}: up to date"); continue; }
            try
            {
                log?.Invoke($"  {pack.name}: {(state == DlcPackState.Corrupt ? "differs from the server's, downloading again" : "downloading")} ({FormatSize(pack.size)}) from {pack.url}");
                await DownloadAsync(paths, pack, null, token);
                downloaded.Add(pack.name);
                log?.Invoke($"  {pack.name}: downloaded and verified");
            }
            catch (Exception ex) when (ex is LauncherException || ex is HttpRequestException || ex is IOException)
            {
                failed.Add((pack.name, ex.Message));
                log?.Invoke($"  {pack.name}: FAILED - {ex.Message}");
            }
        }
        return new PrepareResult(packs, downloaded, upToDate, failed);
    }

    public static string Sha256Of(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string FormatSize(long bytes) => bytes <= 0 ? "unknown size" : bytes < 1 << 20 ? $"{bytes / 1024.0:0.#} KB" : bytes < 1L << 30 ? $"{bytes / 1048576.0:0.#} MB" : $"{bytes / 1073741824.0:0.##} GB";
}
