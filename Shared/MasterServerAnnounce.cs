namespace GTANetworkShared
{
    public class MasterServerAnnounce
    {
        public int Port { get; set; }
        public int MaxPlayers { get; set; }
        public string ServerName { get; set; }
        public int CurrentPlayers { get; set; }
        public string Gamemode { get; set; }
        public string Map { get; set; }
        public bool Passworded { get; set; }
        public string fqdn { get; set; }
        public string ServerVersion { get; set; }
        /// <summary>The server's X25519 public key (hex), for clients to pin (T-009).</summary>
        public string PublicKey { get; set; }
        /// <summary>Owns the address on the master: the first token to announce an address keeps it (master.token next to the server, T-011).</summary>
        public string Token { get; set; }
    }
}