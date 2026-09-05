using System.Collections.Generic;

namespace GTANetworkShared
{
    /// <summary>
    /// One entry of a server's GET /dlcpacks.json (T-014): a custom DLC pack the server wants its players to have. The launcher
    /// downloads <c>url</c> into &lt;install&gt;/dlcpacks/&lt;name&gt;/dlc.rpf and verifies <c>sha256</c> and <c>size</c>;
    /// the client reports the packs it has mounted at connect, and the server refuses players missing a <c>required</c> one.
    /// Lower-case property names: this is the wire shape.
    /// </summary>
    public class DlcPackInfo
    {
        public string name { get; set; }
        public string url { get; set; }
        public string sha256 { get; set; }
        public long size { get; set; }
        public bool required { get; set; }
    }

    public static class DlcPackNames
    {
        /// <summary>A pack name is a folder name: letters, digits, '_' and '-', 1..64 characters.</summary>
        public static bool IsValid(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64) return false;
            foreach (var c in name)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) return false;
            }
            return true;
        }

        /// <summary>The required packs of <paramref name="declared"/> that <paramref name="mounted"/> lacks.</summary>
        public static List<string> Missing(IEnumerable<DlcPackInfo> declared, ICollection<string> mounted)
        {
            var missing = new List<string>();
            if (declared == null) return missing;
            foreach (var pack in declared)
            {
                if (pack == null || !pack.required || string.IsNullOrEmpty(pack.name)) continue;
                if (mounted == null || !mounted.Contains(pack.name)) missing.Add(pack.name);
            }
            return missing;
        }
    }
}
