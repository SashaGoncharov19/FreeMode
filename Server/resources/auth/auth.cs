using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using GTANetworkServer;
using GTANetworkShared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Accounts with registration and login. Until a player logged in, chat messages and every command except
// /register, /login and /help are cancelled and the player is frozen. The client side (client.js + ui/)
// shows a CEF form that calls the RPC handlers "auth:login" / "auth:register" (T-008); the chat commands and the
// older events of the same names run the same code, so every path shares it.
//
// Storage: <resource folder>/accounts.json, one entry per account with a random salt and a PBKDF2-SHA256
// hash (100 000 rounds). Other resources can read the account name of a logged-in player with
// API.getEntityData(player, "auth:account") (null while logged out).
public class Auth : Script
{
    private const int MinPasswordLength = 6;
    private const int HashIterations = 100_000;
    private const int MaxAttemptsPerMinute = 5;
    private static readonly Regex NamePattern = new Regex("^[A-Za-z0-9_]{3,20}$", RegexOptions.Compiled);
    private static readonly HashSet<string> AllowedWhileLoggedOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "register", "login", "help" };

    private class Account
    {
        public string Name;
        public string Salt;       // base64, 16 bytes
        public string Hash;       // base64, 32 bytes, PBKDF2-SHA256
        public int Iterations;
        public DateTime Created;
        public DateTime LastLogin;
    }

    private readonly object _lock = new object();
    private Dictionary<string, Account> _accounts = new Dictionary<string, Account>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Client, string> _sessions = new Dictionary<Client, string>();
    private readonly Dictionary<Client, List<DateTime>> _attempts = new Dictionary<Client, List<DateTime>>();
    private string _storePath;

    public Auth()
    {
        API.onResourceStart += OnResourceStart;
        API.onPlayerConnected += OnPlayerConnected;
        API.onPlayerDisconnected += OnPlayerDisconnected;
        API.onChatMessage += OnChatMessage;
        API.onChatCommand += OnChatCommand;
        API.onClientEventTrigger += OnClientEvent;
        // The CEF form: gtan.rpc.call("auth:login", { name, password }) resolves with { ok, message }.
        API.registerRpc("auth:login", (sender, args) => Attempt(sender, false, args));
        API.registerRpc("auth:register", (sender, args) => Attempt(sender, true, args));
    }

    private object Attempt(Client sender, bool register, object args)
    {
        var fields = args as JObject;
        var name = (string)fields?["name"] ?? "";
        var password = (string)fields?["password"] ?? "";
        var error = register ? Register(sender, name, password) : Login(sender, name, password);
        return new { ok = error == null, message = error ?? (register ? "Account created." : "Logged in.") };
    }

    private void OnResourceStart()
    {
        _storePath = Path.Combine(API.getResourceFolder(), "accounts.json");
        Load();
        API.consoleOutput("auth: " + _accounts.Count + " account(s) loaded from " + _storePath);
    }

    // ---- player lifecycle -------------------------------------------------------------------------------

    private void OnPlayerConnected(Client player)
    {
        lock (_lock)
        {
            _sessions.Remove(player);
        }

        API.freezePlayer(player, true);
        API.sendChatMessageToPlayer(player, "~b~Log in~w~ with ~y~/login <name> <password>~w~ or create an account with ~y~/register <name> <password>~w~.");
    }

    private void OnPlayerDisconnected(Client player, string reason)
    {
        lock (_lock)
        {
            _sessions.Remove(player);
            _attempts.Remove(player);
        }
    }

    private bool IsLoggedIn(Client player)
    {
        lock (_lock)
        {
            return _sessions.ContainsKey(player);
        }
    }

    private void OnChatMessage(Client sender, string message, CancelEventArgs cancel)
    {
        if (IsLoggedIn(sender)) return;

        cancel.Cancel = true;
        API.sendChatMessageToPlayer(sender, "~r~Log in first.~w~ /login <name> <password> or /register <name> <password>");
    }

    private void OnChatCommand(Client sender, string command, CancelEventArgs cancel)
    {
        if (IsLoggedIn(sender)) return;

        var word = command.TrimStart('/').Split(' ')[0];
        if (AllowedWhileLoggedOut.Contains(word)) return;

        cancel.Cancel = true;
        API.sendChatMessageToPlayer(sender, "~r~Log in first.~w~ /login <name> <password> or /register <name> <password>");
    }

    // ---- entry points: chat commands and the CEF form ------------------------------------------------------

    [Command("register", Description = "Create an account: /register <name> <password>")]
    public void RegisterCommand(Client sender, string name, string password)
    {
        Register(sender, name, password);
    }

    [Command("login", Description = "Log in: /login <name> <password>")]
    public void LoginCommand(Client sender, string name, string password)
    {
        Login(sender, name, password);
    }

    private void OnClientEvent(Client sender, string eventName, params object[] arguments)
    {
        if (eventName != "auth:register" && eventName != "auth:login") return;

        var name = arguments.Length > 0 ? arguments[0] as string ?? "" : "";
        var password = arguments.Length > 1 ? arguments[1] as string ?? "" : "";

        if (eventName == "auth:register") Register(sender, name, password);
        else Login(sender, name, password);
    }

    // ---- the logic -------------------------------------------------------------------------------------

    private string Register(Client player, string name, string password)
    {
        if (IsLoggedIn(player))
        {
            return Fail(player, "You are already logged in.");
        }

        if (!NamePattern.IsMatch(name ?? ""))
        {
            return Fail(player, "The name must be 3-20 letters, digits or underscores.");
        }

        if ((password ?? "").Length < MinPasswordLength)
        {
            return Fail(player, "The password must have at least " + MinPasswordLength + " characters.");
        }

        if (!AllowAttempt(player))
        {
            return Fail(player, "Too many attempts, wait a minute.");
        }

        lock (_lock)
        {
            if (_accounts.ContainsKey(name))
            {
                return Fail(player, "That name is taken.");
            }

            var salt = RandomNumberGenerator.GetBytes(16);
            var account = new Account
            {
                Name = name,
                Salt = Convert.ToBase64String(salt),
                Hash = Convert.ToBase64String(Derive(password, salt, HashIterations)),
                Iterations = HashIterations,
                Created = DateTime.UtcNow,
                LastLogin = DateTime.UtcNow,
            };
            _accounts[name] = account;
            Save();
            _sessions[player] = account.Name;
        }

        API.consoleOutput("auth: account " + name + " created by " + player.name);
        API.sendChatMessageToPlayer(player, "~g~Account " + name + " created.~w~ You are logged in.");
        Succeed(player, name, "Welcome, " + name + "! Your account was created.");
        return null;
    }

    private string Login(Client player, string name, string password)
    {
        if (IsLoggedIn(player))
        {
            return Fail(player, "You are already logged in.");
        }

        if (!AllowAttempt(player))
        {
            return Fail(player, "Too many attempts, wait a minute.");
        }

        Account account;
        lock (_lock)
        {
            _accounts.TryGetValue(name ?? "", out account);
        }

        // Same work and same message whether the name exists or the password is wrong.
        var salt = account != null ? Convert.FromBase64String(account.Salt) : new byte[16];
        var expected = account != null ? Convert.FromBase64String(account.Hash) : new byte[32];
        var actual = Derive(password ?? "", salt, account?.Iterations ?? HashIterations);

        if (account == null || !CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return Fail(player, "Wrong name or password.");
        }

        lock (_lock)
        {
            account.LastLogin = DateTime.UtcNow;
            Save();
            _sessions[player] = account.Name;
        }

        API.consoleOutput("auth: " + player.name + " logged in as " + account.Name);
        API.sendChatMessageToPlayer(player, "~g~Logged in as " + account.Name + ".");
        Succeed(player, account.Name, "Welcome back, " + account.Name + "!");
        return null;
    }

    private void Succeed(Client player, string accountName, string message)
    {
        API.setEntityData(player, "auth:account", accountName);
        API.freezePlayer(player, false);
        API.triggerClientEvent(player, "auth:result", true, message);
    }

    /// <summary>Tells the player why it failed (chat and the "auth:result" event) and returns the reason for the RPC answer.</summary>
    private string Fail(Client player, string reason)
    {
        API.sendChatMessageToPlayer(player, "~r~" + reason);
        API.triggerClientEvent(player, "auth:result", false, reason);
        return reason;
    }

    private bool AllowAttempt(Client player)
    {
        lock (_lock)
        {
            List<DateTime> list;
            if (!_attempts.TryGetValue(player, out list))
            {
                list = new List<DateTime>();
                _attempts[player] = list;
            }

            var now = DateTime.UtcNow;
            list.RemoveAll(t => (now - t).TotalSeconds > 60);
            if (list.Count >= MaxAttemptsPerMinute) return false;

            list.Add(now);
            return true;
        }
    }

    private static byte[] Derive(string password, byte[] salt, int iterations)
    {
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
    }

    // ---- storage ---------------------------------------------------------------------------------------

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_storePath)) return;

            var list = JsonConvert.DeserializeObject<List<Account>>(File.ReadAllText(_storePath)) ?? new List<Account>();
            _accounts = list.Where(a => !string.IsNullOrEmpty(a.Name)).ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        // Caller holds _lock. Write to a temporary file first so a crash never leaves a half-written store.
        var temp = _storePath + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(_accounts.Values.OrderBy(a => a.Created).ToList(), Formatting.Indented));
        File.Move(temp, _storePath, true);
    }
}
