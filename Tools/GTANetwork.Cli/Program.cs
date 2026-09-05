using System.Reflection;
using System.Text.RegularExpressions;

// gtanetwork create <name> [--dir <parent>] [--force]: a resource skeleton in TypeScript (templates/resource with __NAME__
// replaced, plus the typings the resource type-checks against, copied so the folder is self-contained).

var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Usage();
    return args.Length == 0 ? 1 : 0;
}

switch (args[0])
{
    case "--version":
        Console.WriteLine("gtanetwork " + version);
        return 0;
    case "create":
        return Create(args.Skip(1).ToArray());
    default:
        Console.Error.WriteLine("unknown command: " + args[0]);
        Usage();
        return 1;
}

static void Usage()
{
    Console.WriteLine("""
        gtanetwork - the GTA Network command line

          gtanetwork create <name> [--dir <parent>] [--force]
              Write a resource skeleton in TypeScript into <parent>/<name> (default: the current directory):
              meta.xml, server/index.ts (Bun runtime), client/index.ts (bundled by the server), ui/ (a CEF page),
              types/ (the typings the checks use), package.json with `bun run check`. --force writes into a folder
              that already has files.
          gtanetwork --version
        """);
}

static int Create(string[] args)
{
    string? name = null, parent = null;
    var force = false;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--dir": parent = i + 1 < args.Length ? args[++i] : null; break;
            case "--force": force = true; break;
            default:
                if (args[i].StartsWith('-') || name != null) { Console.Error.WriteLine("unexpected argument: " + args[i]); return 1; }
                name = args[i];
                break;
        }
    }
    if (string.IsNullOrEmpty(name)) { Console.Error.WriteLine("create needs a name: gtanetwork create <name>"); return 1; }
    if (!Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_-]{0,31}$")) { Console.Error.WriteLine("the name must start with a letter and contain letters, digits, '_' or '-' (2-32 characters)"); return 1; }

    var templates = Locate("templates", "resource", "meta.xml");
    var typings = Locate("typings", null, "client.d.ts") ?? Locate("types", null, "client.d.ts");
    if (templates == null) { Console.Error.WriteLine("templates/resource not found next to the executable (is the installation complete?)"); return 2; }
    if (typings == null) { Console.Error.WriteLine("the typings (typings/ next to the executable, or types/ in a checkout) were not found"); return 2; }

    var target = Path.GetFullPath(Path.Combine(parent ?? Directory.GetCurrentDirectory(), name));
    if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any() && !force)
    {
        Console.Error.WriteLine(target + " exists and is not empty (use --force to write into it)");
        return 2;
    }

    var written = new List<string>();
    foreach (var file in Directory.EnumerateFiles(templates, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(templates, file);
        var destination = Path.Combine(target, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, File.ReadAllText(file).Replace("__NAME__", name));
        written.Add(relative.Replace('\\', '/'));
    }
    // the typings: shared/client/cef from the client build, the server API and enums from the runtime library
    var typingsDir = Path.Combine(target, "types");
    Directory.CreateDirectory(typingsDir);
    foreach (var typing in new[] { "shared.d.ts", "client.d.ts", "cef.d.ts", "api.generated.d.ts", "enums.generated.ts" })
    {
        var source = FindTyping(typings, typing);
        if (source == null) { Console.Error.WriteLine("typing not found: " + typing); return 2; }
        File.Copy(source, Path.Combine(typingsDir, typing), true);
        written.Add("types/" + typing);
    }

    Console.WriteLine("Created the resource \"" + name + "\" in " + target + ":");
    foreach (var file in written.OrderBy(f => f, StringComparer.Ordinal)) Console.WriteLine("  " + file);
    Console.WriteLine();
    Console.WriteLine("Next:");
    Console.WriteLine("  cd " + name + " && bun install && bun run check        type-check both sides (Bun: https://bun.sh)");
    Console.WriteLine("  move the folder into <server>/resources/ and add <resource src=\"" + name + "\" /> to the server's settings.xml");
    Console.WriteLine("  start the server: it bundles client/index.ts and runs server/index.ts in Bun; then /hello and /panel in game");
    return 0;
}

/// <summary>A folder next to the executable, or (development) up the tree from it: e.g. templates/resource with meta.xml in it.</summary>
static string? Locate(string folder, string? sub, string marker)
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
    {
        var candidate = sub == null ? Path.Combine(dir.FullName, folder) : Path.Combine(dir.FullName, folder, sub);
        if (File.Exists(Path.Combine(candidate, marker))) return candidate;
    }
    return null;
}

/// <summary>The typing in the shipped typings folder, or in a checkout (types/ and runtime/gtan/).</summary>
static string? FindTyping(string typings, string file)
{
    var direct = Path.Combine(typings, file);
    if (File.Exists(direct)) return direct;
    var checkoutRoot = Directory.GetParent(typings)?.FullName;
    if (checkoutRoot == null) return null;
    var runtime = Path.Combine(checkoutRoot, "runtime", "gtan", file);
    return File.Exists(runtime) ? runtime : null;
}
