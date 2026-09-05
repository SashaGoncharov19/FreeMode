using Microsoft.Data.Sqlite;

namespace GTANetwork.Master;

/// <summary>One announced server as the master keeps it.</summary>
public sealed record ServerRow(
    string Address, string Name, int Players, int MaxPlayers, string Gamemode, string Map, bool Passworded,
    string Version, string PublicKey, bool Verified, DateTimeOffset LastSeen, DateTimeOffset? LastPingOk, string Token);

/// <summary>SQLite storage of the announced servers; one file, one table, one writer at a time.</summary>
public sealed class Db
{
    private readonly string _connectionString;
    private readonly object _lock = new();

    public Db(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "master.db"), Cache = SqliteCacheMode.Shared }.ToString();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS servers (
                address TEXT PRIMARY KEY, token TEXT NOT NULL, name TEXT NOT NULL, players INTEGER NOT NULL, max_players INTEGER NOT NULL,
                gamemode TEXT NOT NULL, map TEXT NOT NULL, passworded INTEGER NOT NULL, version TEXT NOT NULL, public_key TEXT NOT NULL,
                last_seen TEXT NOT NULL, last_ping_ok TEXT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    /// <summary>The token of an address as first announced; null when the address is new.</summary>
    public string? TokenOf(string address)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT token FROM servers WHERE address = $a";
            cmd.Parameters.AddWithValue("$a", address);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void Upsert(ServerRow row)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO servers (address, token, name, players, max_players, gamemode, map, passworded, version, public_key, last_seen, last_ping_ok)
                VALUES ($a, $t, $n, $p, $m, $g, $map, $pw, $v, $k, $seen, $ping)
                ON CONFLICT(address) DO UPDATE SET token = $t, name = $n, players = $p, max_players = $m, gamemode = $g, map = $map,
                    passworded = $pw, version = $v, public_key = $k, last_seen = $seen, last_ping_ok = COALESCE($ping, servers.last_ping_ok);
                """;
            cmd.Parameters.AddWithValue("$a", row.Address);
            cmd.Parameters.AddWithValue("$t", row.Token);
            cmd.Parameters.AddWithValue("$n", row.Name);
            cmd.Parameters.AddWithValue("$p", row.Players);
            cmd.Parameters.AddWithValue("$m", row.MaxPlayers);
            cmd.Parameters.AddWithValue("$g", row.Gamemode);
            cmd.Parameters.AddWithValue("$map", row.Map);
            cmd.Parameters.AddWithValue("$pw", row.Passworded ? 1 : 0);
            cmd.Parameters.AddWithValue("$v", row.Version);
            cmd.Parameters.AddWithValue("$k", row.PublicKey);
            cmd.Parameters.AddWithValue("$seen", row.LastSeen.ToString("o"));
            cmd.Parameters.AddWithValue("$ping", (object?)row.LastPingOk?.ToString("o") ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public void SetPingResult(string address, bool ok, DateTimeOffset when)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = ok ? "UPDATE servers SET last_ping_ok = $w WHERE address = $a" : "UPDATE servers SET last_ping_ok = NULL WHERE address = $a";
            cmd.Parameters.AddWithValue("$a", address);
            cmd.Parameters.AddWithValue("$w", when.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    public int Prune(DateTimeOffset olderThan)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM servers WHERE last_seen < $t";
            cmd.Parameters.AddWithValue("$t", olderThan.ToString("o"));
            return cmd.ExecuteNonQuery();
        }
    }

    public List<ServerRow> All(ISet<string> verified)
    {
        lock (_lock)
        {
            var rows = new List<ServerRow>();
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT address, token, name, players, max_players, gamemode, map, passworded, version, public_key, last_seen, last_ping_ok FROM servers ORDER BY players DESC, name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var address = r.GetString(0);
                rows.Add(new ServerRow(address, r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetString(5), r.GetString(6), r.GetInt32(7) != 0,
                    r.GetString(8), r.GetString(9), verified.Contains(address), DateTimeOffset.Parse(r.GetString(10)),
                    r.IsDBNull(11) ? null : DateTimeOffset.Parse(r.GetString(11)), r.GetString(1)));
            }
            return rows;
        }
    }
}
