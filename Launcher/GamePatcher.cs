using System.Xml.Linq;

namespace GTANetwork.Launcher;

/// <summary>The same tweaks the classic launcher applied to the player's GTA V profile.</summary>
internal static class GamePatcher
{
    /// <summary>"Documents\Rockstar Games\GTA V" of the user that runs the game (inside the Wine prefix on Linux).</summary>
    public static string? DocumentsDir(string? prefix)
    {
        if (OperatingSystem.IsWindows())
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rockstar Games", "GTA V");
            return Directory.Exists(dir) ? dir : null;
        }

        if (string.IsNullOrEmpty(prefix)) return null;

        var users = Path.Combine(prefix, "drive_c", "users");
        if (!Directory.Exists(users)) return null;

        foreach (var user in new[] { "steamuser", Environment.UserName }.Concat(Directory.GetDirectories(users).Select(Path.GetFileName)!).Distinct())
        {
            foreach (var documents in new[] { "Documents", "My Documents" })
            {
                var dir = Path.Combine(users, user!, documents, "Rockstar Games", "GTA V");
                if (Directory.Exists(dir)) return dir;
            }
        }

        return null;
    }

    public static void Patch(string? documentsDir)
    {
        if (documentsDir == null)
        {
            Log.Warn("GTA V profile folder not found (run the game once without GTA Network first). Skipping settings patch.");
            return;
        }

        PatchSettingsXml(Path.Combine(documentsDir, "settings.xml"));

        var profiles = Path.Combine(documentsDir, "Profiles");
        if (Directory.Exists(profiles))
        {
            foreach (var pcSettings in Directory.GetFiles(profiles, "pc_settings.bin", SearchOption.AllDirectories))
                PatchStartup(pcSettings);
        }
    }

    /// <summary>Keep rendering while unfocused (needed for the overlay/CEF) and force DirectX 11.</summary>
    private static void PatchSettingsXml(string path)
    {
        if (!File.Exists(path)) return;

        try
        {
            var doc = XDocument.Load(path);
            var changed = false;

            var pause = doc.Descendants("PauseOnFocusLoss").FirstOrDefault();
            if (pause != null && (string?)pause.Attribute("value") != "0") { pause.SetAttributeValue("value", 0); changed = true; }

            var dx = doc.Descendants("DX_Version").FirstOrDefault();
            if (dx != null && (string?)dx.Attribute("value") != "2") { dx.SetAttributeValue("value", 2); changed = true; }

            if (changed)
            {
                File.Copy(path, path + ".gtan.bak", true);
                doc.Save(path);
                Log.Info("Patched game settings.xml (PauseOnFocusLoss=0, DX_Version=2).");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not patch {path}: {ex.Message}");
        }
    }

    /// <summary>Skip the landing page / startup flow so the game boots straight into the world.</summary>
    private static void PatchStartup(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length <= 0xF4) return;
            if (bytes[0xF4] == 0 && bytes[0xEC] == 0) return;

            if (!File.Exists(path + ".gtan.bak")) File.Copy(path, path + ".gtan.bak");
            bytes[0xF4] = 0; // startup flow
            bytes[0xEC] = 0; // landing page
            File.WriteAllBytes(path, bytes);
            Log.Info("Patched " + path + " (skip landing page).");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not patch {path}: {ex.Message}");
        }
    }
}
