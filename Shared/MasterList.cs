namespace GTANetworkShared
{
    /// <summary>One row of the master list's GET /servers/full (Tools/GTANetwork.Master, T-011). Lower-case property names: the wire shape.</summary>
    public class MasterServerRow
    {
        public string address { get; set; }
        public string name { get; set; }
        public int players { get; set; }
        public int maxPlayers { get; set; }
        public string gamemode { get; set; }
        public string map { get; set; }
        public bool passworded { get; set; }
        public string version { get; set; }
        public string publicKey { get; set; }
        public bool verified { get; set; }
        public string lastSeen { get; set; }
    }
}
