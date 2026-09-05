using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace GTANetwork.Util
{
    /// <summary>The DLC packs the launcher applied for this game session (T-014): &lt;install&gt;\dlcpacks\mounted.json, a JSON array of names.</summary>
    internal static class DlcPacks
    {
        public static List<string> Mounted()
        {
            try
            {
                var path = Path.Combine(Main.GTANInstallDir, "dlcpacks", "mounted.json");
                if (!File.Exists(path)) return null;
                var names = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(path));
                return names == null || names.Count == 0 ? null : names;
            }
            catch (Exception ex)
            {
                LogManager.RuntimeLog("dlcpacks: mounted.json could not be read: " + ex.Message);
                return null;
            }
        }
    }
}
