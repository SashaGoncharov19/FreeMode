using System.Net.Http;
using System.Text.Json;
using GTANetworkShared;

namespace GTANetwork.Launcher;

/// <summary>The launcher's server list (T-024): the master's GET /servers/full plus the favourites and recent servers of settings.xml.</summary>
public static class ServerList
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public sealed record Entry(string Address, string Name, int Players, int MaxPlayers, string Gamemode, string Version, bool Verified, bool Passworded, string? PublicKey, bool Favorite, bool Recent, bool OnMaster);

    /// <summary>GET {master}/servers/full; an empty list when no master is configured.</summary>
    public static async Task<List<MasterServerRow>> FetchAsync(string? masterAddress, CancellationToken token = default)
    {
        var master = (masterAddress ?? "").Trim().TrimEnd('/');
        if (master.Length == 0) return new List<MasterServerRow>();
        if (!master.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !master.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) master = "http://" + master;
        var json = await Http.GetStringAsync(master + "/servers/full", token);
        return JsonSerializer.Deserialize<List<MasterServerRow>>(json, JsonOptions) ?? new List<MasterServerRow>();
    }

    /// <summary>The rows to show: every master row, then favourites and recent servers the master does not know (offline or LAN).</summary>
    public static List<Entry> Merge(IEnumerable<MasterServerRow> master, PlayerSettings settings)
    {
        var favorites = new HashSet<string>(settings.FavoriteServers ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var recent = new HashSet<string>(settings.RecentServers ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var rows = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in master)
        {
            if (string.IsNullOrWhiteSpace(m.address) || !seen.Add(m.address)) continue;
            rows.Add(new Entry(m.address, string.IsNullOrWhiteSpace(m.name) ? m.address : m.name, m.players, m.maxPlayers, m.gamemode ?? "", m.version ?? "", m.verified, m.passworded, string.IsNullOrEmpty(m.publicKey) ? null : m.publicKey, favorites.Contains(m.address), recent.Contains(m.address), true));
        }
        foreach (var address in favorites.Concat(recent))
        {
            if (!seen.Add(address)) continue;
            rows.Add(new Entry(address, address, 0, 0, "", "", false, false, null, favorites.Contains(address), recent.Contains(address), false));
        }
        return rows.OrderByDescending(r => r.Favorite).ThenByDescending(r => r.OnMaster).ThenByDescending(r => r.Players).ThenBy(r => r.Name).ToList();
    }

    /// <summary>What the client reads from GTAN_CONNECT: host:port, "#key" when the master knows the server's key.</summary>
    public static string ConnectTarget(string address, string? publicKey) => string.IsNullOrEmpty(publicKey) ? address : address + "#" + publicKey;

    public static void RememberRecent(PlayerSettings settings, string address)
    {
        settings.RecentServers ??= new List<string>();
        settings.RecentServers.RemoveAll(a => string.Equals(a, address, StringComparison.OrdinalIgnoreCase));
        settings.RecentServers.Insert(0, address);
        if (settings.RecentServers.Count > 20) settings.RecentServers.RemoveRange(20, settings.RecentServers.Count - 20);
    }

    public static bool ToggleFavorite(PlayerSettings settings, string address)
    {
        settings.FavoriteServers ??= new List<string>();
        var removed = settings.FavoriteServers.RemoveAll(a => string.Equals(a, address, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed) settings.FavoriteServers.Add(address);
        return !removed;
    }
}
