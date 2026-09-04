using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace GTANetwork.TypeGen;

/// <summary>
/// Entry point. Arguments:
///   --client &lt;GTANetwork.dll&gt;         the in-game client (net48); needs --net48-refs
///   --server &lt;GTANetworkServer.dll&gt;   the server (net10.0)
///   --net48-refs &lt;dir&gt;                .NET Framework 4.8 reference assemblies (the NuGet package
///                                     Microsoft.NETFramework.ReferenceAssemblies.net48, build/.NETFramework/v4.8)
///   --out &lt;dir&gt;                       output folder (default: types)
/// Every DLL next to the inspected assembly is offered to the resolver, so dependencies resolve without being loaded.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string? client = null, server = null, net48Refs = null, outDir = "types";
        var probe = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException("missing value after " + args[i - 1]);
            switch (args[i])
            {
                case "--client": client = Next(); break;
                case "--server": server = Next(); break;
                case "--net48-refs": net48Refs = Next(); break;
                case "--probe": probe.Add(Next()); break; // more folders with referenced assemblies (e.g. the SHVDN stub, not copied next to the client)
                case "--out": outDir = Next(); break;
                default: Console.Error.WriteLine("unknown argument " + args[i]); return 2;
            }
        }
        if (client == null && server == null)
        {
            Console.Error.WriteLine("nothing to do: pass --client and/or --server");
            return 2;
        }
        Directory.CreateDirectory(outDir);

        var shared = new Emitter("shared");
        var report = new StringBuilder();

        if (client != null)
        {
            if (net48Refs == null || !Directory.Exists(net48Refs)) { Console.Error.WriteLine("--net48-refs <dir> is required with --client"); return 2; }
            using var ctx = Load(client, [net48Refs, Path.Combine(net48Refs, "Facades"), .. probe], "mscorlib");
            var asm = ctx.LoadFromAssemblyPath(Path.GetFullPath(client));
            var scriptContext = asm.GetType("GTANetwork.Javascript.ScriptContext") ?? throw new InvalidOperationException("GTANetwork.Javascript.ScriptContext not found");
            var emitter = new Emitter("client", shared);
            emitter.Docs.Load(client);
            emitter.EmitRoot(scriptContext, "ScriptContext");
            emitter.EmitClientGlobals(asm, ctx);
            emitter.Flush();
            File.WriteAllText(Path.Combine(outDir, "client.d.ts"), emitter.Render("GTANetwork.dll (in-game client)", asm.GetName().Version, "/// <reference path=\"./shared.d.ts\" />"), new UTF8Encoding(false));
            report.AppendLine($"client: {emitter.MemberCount} members of ScriptContext, {emitter.TypeCount} types, {emitter.Skipped} skipped");
        }

        if (server != null)
        {
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
            using var ctx = Load(server, [runtimeDir, .. probe], "System.Private.CoreLib");
            var asm = ctx.LoadFromAssemblyPath(Path.GetFullPath(server));
            var api = asm.GetType("GTANetworkServer.API") ?? throw new InvalidOperationException("GTANetworkServer.API not found");
            var emitter = new Emitter("server", shared);
            emitter.Docs.Load(server);
            emitter.EmitRoot(api, "API");
            emitter.EmitServerGlobals(asm);
            emitter.Flush();
            File.WriteAllText(Path.Combine(outDir, "server.d.ts"), emitter.Render("GTANetworkServer.dll", asm.GetName().Version, "/// <reference path=\"./shared.d.ts\" />"), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(outDir, "api-catalogue.json"), Catalogue.Build(api, emitter), new UTF8Encoding(false));
            report.AppendLine($"server: {emitter.MemberCount} members of API, {emitter.TypeCount} types, {emitter.Skipped} skipped, catalogue written");
        }

        shared.Flush();
        File.WriteAllText(Path.Combine(outDir, "shared.d.ts"), shared.Render("GTANetworkShared.dll and the types both APIs use", null, Prelude.Text), new UTF8Encoding(false));
        report.AppendLine($"shared: {shared.TypeCount} types");
        Console.Write(report.ToString());
        return 0;
    }

    private static MetadataLoadContext Load(string assemblyPath, string[] extraDirs, string coreAssemblyName)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*.dll")) paths.Add(Path.GetFullPath(f));
        }
        AddDir(Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!);
        foreach (var d in extraDirs) AddDir(d);
        return new MetadataLoadContext(new PathAssemblyResolver(paths), coreAssemblyName);
    }
}

/// <summary>The hand-written part of shared.d.ts: the event shape ClearScript exposes and the framework types the APIs use.</summary>
internal static class Prelude
{
    public const string Text = """
/** A .NET event as ClearScript exposes it: `API.onUpdate.connect(handler)`. */
interface HostEvent<T extends (...args: any[]) => void> {
    connect(handler: T): void;
    disconnect(handler: T): void;
}
/** System.ComponentModel.CancelEventArgs */
interface CancelEventArgs { Cancel: boolean; }
/** System.Windows.Forms.KeyEventArgs (client key events) */
interface KeyEventArgs { KeyCode: Keys; KeyData: Keys; KeyValue: number; Modifiers: Keys; Alt: boolean; Control: boolean; Shift: boolean; Handled: boolean; SuppressKeyPress: boolean; }
/** System.Drawing.Point / PointF / Size / SizeF / Color */
interface Point { X: number; Y: number; }
interface PointF { X: number; Y: number; }
interface Size { Width: number; Height: number; }
interface SizeF { Width: number; Height: number; }
interface Color { R: number; G: number; B: number; A: number; }
/** System.TimeSpan as a host object */
interface TimeSpan { Ticks: number; TotalMilliseconds: number; TotalSeconds: number; TotalMinutes: number; }
interface EventArgs {}
/** SharpDX.Size2 (client) */
interface Size2 { Width: number; Height: number; }
/** System.Xml.Linq.XElement as seen by scripts (server map XML) */
type XElement = unknown;
""";
}

/// <summary>Reads the compiler's XML documentation file next to an assembly, when it exists.</summary>
internal sealed class XmlDocs
{
    private readonly Dictionary<string, string> _summaries = new();

    public void Load(string assemblyPath)
    {
        var xml = Path.ChangeExtension(assemblyPath, ".xml");
        if (!File.Exists(xml)) return;
        try
        {
            foreach (var m in XDocument.Load(xml).Descendants("member"))
            {
                var name = m.Attribute("name")?.Value;
                var summary = m.Element("summary")?.Value;
                if (name == null || string.IsNullOrWhiteSpace(summary)) continue;
                _summaries[name] = string.Join(" ", summary.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("warning: could not read " + xml + ": " + ex.Message);
        }
    }

    public string? For(MemberInfo member)
    {
        var id = Id(member);
        if (id != null && _summaries.TryGetValue(id, out var s)) return s;
        if (member is MethodInfo mi && mi.DeclaringType != null)
        {
            // fall back to the first overload documented
            var prefix = "M:" + mi.DeclaringType.FullName + "." + mi.Name + "(";
            var hit = _summaries.Keys.FirstOrDefault(k => k.StartsWith(prefix, StringComparison.Ordinal));
            if (hit != null) return _summaries[hit];
        }
        return null;
    }

    private static string? Id(MemberInfo member)
    {
        var declaring = member.DeclaringType?.FullName?.Replace('+', '.');
        if (declaring == null) return null;
        switch (member)
        {
            case Type t: return "T:" + t.FullName?.Replace('+', '.');
            case PropertyInfo p: return "P:" + declaring + "." + p.Name;
            case EventInfo e: return "E:" + declaring + "." + e.Name;
            case FieldInfo f: return "F:" + declaring + "." + f.Name;
            case MethodInfo m:
                var ps = m.GetParameters();
                return "M:" + declaring + "." + m.Name + (ps.Length == 0 ? "" : "(" + string.Join(",", ps.Select(p => ParamId(p.ParameterType))) + ")");
        }
        return null;
    }

    private static string ParamId(Type t)
    {
        if (t.IsByRef) return ParamId(t.GetElementType()!) + "@";
        if (t.IsArray) return ParamId(t.GetElementType()!) + "[]";
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition().FullName!;
            def = def[..def.IndexOf('`')];
            return def + "{" + string.Join(",", t.GetGenericArguments().Select(ParamId)) + "}";
        }
        return (t.FullName ?? t.Name).Replace('+', '.');
    }
}

/// <summary>Turns the public surface of a root type (and every type it references) into TypeScript declarations.</summary>
internal sealed class Emitter
{
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "break","case","catch","class","const","continue","debugger","default","delete","do","else","enum","export","extends","false","finally","for","function","if","import","in","instanceof","new","null","return","super","switch","this","throw","true","try","typeof","var","void","while","with","yield","let","static","implements","interface","package","private","protected","public","await","arguments","eval","any","boolean","number","string","symbol","type","of",
    };

    private static readonly HashSet<string> SkipMembers = new(StringComparer.Ordinal) { "ToString", "Equals", "GetHashCode", "GetType", "Finalize", "MemberwiseClone", "Dispose" };

    private readonly string _side;
    private readonly Emitter? _shared;
    private readonly Queue<Type> _pending = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal); // full name -> TS name
    private readonly HashSet<string> _usedNames; // shared by every emitter: TypeScript merges same-named declarations across files
    private readonly StringBuilder _out = new();
    private readonly StringBuilder _globals = new();

    public readonly XmlDocs Docs = new();
    public int MemberCount { get; private set; }
    public int TypeCount { get; private set; }
    public int Skipped { get; private set; }

    public Emitter(string side, Emitter? shared = null)
    {
        _side = side;
        _shared = shared;
        _usedNames = shared?._usedNames ?? new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in new[] { "HostEvent", "CancelEventArgs", "KeyEventArgs", "Point", "PointF", "Size", "SizeF", "Color", "Size2", "TimeSpan", "EventArgs", "XElement", "Keys" }) _usedNames.Add(n);
    }

    /// <summary>Emit the root API type and enqueue everything it refers to.</summary>
    public void EmitRoot(Type root, string name)
    {
        _names[root.FullName!] = name;
        _usedNames.Add(name);
        var members = EmitInterface(root, name, isRoot: true);
        MemberCount = members;
        _seen.Add(root.FullName!);
    }

    public void EmitClientGlobals(Assembly clientAssembly, MetadataLoadContext ctx)
    {
        _globals.AppendLine("/** The scripting API object of the running script (one ScriptContext per script file). */");
        _globals.AppendLine("declare const API: ScriptContext;");
        _globals.AppendLine("/** Other scripts of the same resource (by file name without extension) and their exported members. */");
        _globals.AppendLine("declare const resource: Record<string, any>;");
        _globals.AppendLine("/** Exported members of every running resource, by resource name. */");
        _globals.AppendLine("declare const exported: Record<string, any>;");
        _globals.AppendLine("/** Host types the client registers with AddHostType; generic host types are constructed as `new (List(Int32))()`. */");
        _globals.AppendLine("declare const List: any;");
        _globals.AppendLine("declare const Dictionary: any;");
        _globals.AppendLine("declare const Enumerable: any;");
        _globals.AppendLine("// `String` is a host type too, but it is a JavaScript global already and is not redeclared here.");
        foreach (var (host, clr) in new[] { ("Int32", "number"), ("Bool", "boolean"), ("Double", "number"), ("Float", "number") })
            _globals.AppendLine($"declare const {host}: {{ new (value?: any): {clr} }};");
        var vector3 = Map(ResolveShared(clientAssembly, ctx, "GTANetworkShared.Vector3"));
        var matrix4 = Map(ResolveShared(clientAssembly, ctx, "GTANetworkShared.Matrix4"));
        _globals.AppendLine($"declare const Vector3: {{ new (): {vector3}; new (x: number, y: number, z: number): {vector3}; }};");
        _globals.AppendLine($"declare const Matrix4: {{ new (): {matrix4}; }};");
        _globals.AppendLine("declare const Point: { new (x: number, y: number): Point; };");
        _globals.AppendLine("declare const PointF: { new (x: number, y: number): PointF; };");
        _globals.AppendLine("declare const Size: { new (width: number, height: number): Size; };");
        _globals.AppendLine("declare const Size2: { new (width: number, height: number): Size2; };");
        _globals.AppendLine("declare const KeyEventArgs: { new (keyData: Keys): KeyEventArgs; };");
        _globals.AppendLine("declare const CancelEventArgs: { new (cancel?: boolean): CancelEventArgs; };");
        // enums registered as host types
        var menuControls = clientAssembly.GetReferencedAssemblies().Select(a => TryLoad(ctx, a)).FirstOrDefault(a => a?.GetName().Name == "NativeUI")?.GetType("NativeUI.UIMenu+MenuControls");
        var badge = clientAssembly.GetReferencedAssemblies().Select(a => TryLoad(ctx, a)).FirstOrDefault(a => a?.GetName().Name == "NativeUI")?.GetType("NativeUI.UIMenuItem+BadgeStyle");
        if (menuControls != null) _globals.AppendLine($"declare const menuControl: typeof {Map(menuControls)};");
        if (badge != null) _globals.AppendLine($"declare const BadgeStyle: typeof {Map(badge)};");
    }

    public void EmitServerGlobals(Assembly serverAssembly)
    {
        _globals.AppendLine("/** The API instance a Script sees (`API` inside a class deriving from Script; the Bun runtime library exposes the same shape). */");
        _globals.AppendLine("declare const API: API;");
        var script = serverAssembly.GetType("GTANetworkServer.Script");
        if (script != null) _globals.AppendLine("declare abstract class Script { API: API; }");
    }

    private static Assembly? TryLoad(MetadataLoadContext ctx, AssemblyName name)
    {
        try { return ctx.LoadFromAssemblyName(name); } catch { return null; }
    }

    private static Type ResolveShared(Assembly from, MetadataLoadContext ctx, string fullName)
    {
        foreach (var r in from.GetReferencedAssemblies())
        {
            var a = TryLoad(ctx, r);
            var t = a?.GetType(fullName);
            if (t != null) return t;
        }
        throw new InvalidOperationException(fullName + " not found among the references of " + from.GetName().Name);
    }

    /// <summary>Drains the queue of referenced types, emitting each once (shared types go to the shared emitter).</summary>
    public void Flush()
    {
        while (_pending.Count > 0)
        {
            var t = _pending.Dequeue();
            var key = t.FullName!;
            if (!_seen.Add(key)) continue;
            var name = _names[key];
            if (t.IsEnum) EmitEnum(t, name);
            else if (typeof(MulticastDelegate).FullName == t.BaseType?.FullName) EmitDelegate(t, name);
            else EmitInterface(t, name, isRoot: false);
            TypeCount++;
        }
        _shared?.Flush();
    }

    public string Render(string source, Version? version, string header)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Generated by Tools/GTANetwork.TypeGen from " + source + (version != null ? " " + version : "") + ". Do not edit; run the generator.");
        sb.AppendLine(header);
        sb.AppendLine();
        sb.Append(_out);
        if (_globals.Length > 0)
        {
            sb.AppendLine();
            sb.Append(_globals);
        }
        return sb.ToString().Replace("\r\n", "\n");
    }

    // ---- type mapping ----

    /// <summary>The TypeScript type for a CLR type; our own types are queued for emission.</summary>
    public string Map(Type t)
    {
        if (t.IsByRef || t.IsPointer) return t.IsPointer ? "unknown" : Map(t.GetElementType()!);
        if (t.IsGenericParameter) return t.Name;
        if (t.IsArray) return Wrap(Map(t.GetElementType()!)) + "[]";
        var full = t.IsGenericType ? t.GetGenericTypeDefinition().FullName : t.FullName;
        switch (full)
        {
            case "System.Void": return "void";
            case "System.Boolean": return "boolean";
            case "System.String": case "System.Char": return "string";
            case "System.Byte": case "System.SByte": case "System.Int16": case "System.UInt16": case "System.Int32": case "System.UInt32":
            case "System.Int64": case "System.UInt64": case "System.Single": case "System.Double": case "System.Decimal": return "number";
            case "System.Object": return "unknown";
            case "System.DateTime": return "Date";
            case "System.Threading.Tasks.Task": return "Promise<void>";
            case "System.Threading.Tasks.Task`1": return "Promise<" + Map(t.GetGenericArguments()[0]) + ">";
            case "System.Nullable`1": return Map(t.GetGenericArguments()[0]) + " | null";
            case "System.Collections.Generic.List`1": case "System.Collections.Generic.IList`1": case "System.Collections.Generic.IEnumerable`1":
            case "System.Collections.Generic.ICollection`1": case "System.Collections.Generic.IReadOnlyList`1": case "System.Collections.Generic.HashSet`1":
                return Wrap(Map(t.GetGenericArguments()[0])) + "[]";
            case "System.Collections.Generic.Dictionary`2": case "System.Collections.Generic.IDictionary`2":
                var k = Map(t.GetGenericArguments()[0]);
                var v = Map(t.GetGenericArguments()[1]);
                return k is "string" or "number" ? "Record<" + k + ", " + v + ">" : "Map<" + k + ", " + v + ">";
            case "System.Collections.Generic.KeyValuePair`2":
                return "{ Key: " + Map(t.GetGenericArguments()[0]) + "; Value: " + Map(t.GetGenericArguments()[1]) + " }";
            case "System.ComponentModel.CancelEventArgs": return "CancelEventArgs";
            case "System.Windows.Forms.KeyEventArgs": return "KeyEventArgs";
            case "System.Drawing.Point": return "Point";
            case "System.Drawing.PointF": return "PointF";
            case "System.Drawing.Size": return "Size";
            case "System.Drawing.SizeF": return "SizeF";
            case "System.Drawing.Color": return "Color";
            case "System.TimeSpan": return "TimeSpan";
            case "System.EventArgs": return "EventArgs";
            case "SharpDX.Size2": return "Size2";
            case "System.Xml.Linq.XElement": return "XElement";
        }
        if (!IsOurs(t))
        {
            // enums of other assemblies (System.Windows.Forms.Keys, GTA.Control, GTA.UI.Font, ...): plain numbers, emitted into shared.d.ts
            if (t.IsEnum) return (_shared ?? this).QueueForeignEnum(t);
            // framework delegates (System.EventHandler, KeyEventHandler, Action<>, Func<>): an inline function type
            if (t.BaseType?.FullName == "System.MulticastDelegate")
            {
                var invoke = t.GetMethod("Invoke");
                if (invoke != null) return "(" + Params(invoke.GetParameters()) + ") => " + Map(invoke.ReturnType);
            }
            return "unknown /* " + full + " */";
        }
        return Queue(t);
    }

    private static string Wrap(string ts) => ts.Contains(" | ") || ts.Contains(" => ") ? "(" + ts + ")" : ts;

    private static bool IsOurs(Type t)
    {
        var asm = t.Assembly.GetName().Name ?? "";
        return asm.StartsWith("GTANetwork", StringComparison.Ordinal) || asm == "NativeUI";
    }

    private static bool IsShared(Type t) => t.Assembly.GetName().Name == "GTANetworkShared";

    private string QueueForeignEnum(Type t)
    {
        var key = t.FullName!;
        if (_names.TryGetValue(key, out var existing)) return existing;
        var name = key == "System.Windows.Forms.Keys" ? "Keys" : Unique(t.Name, t.Namespace);
        _names[key] = name;
        _pending.Enqueue(t);
        return name;
    }

    /// <summary>A TypeScript name not used yet: the simple name, else prefixed with the last namespace segment, else suffixed.</summary>
    private string Unique(string simple, string? ns)
    {
        var name = simple;
        if (_usedNames.Contains(name) && !string.IsNullOrEmpty(ns))
        {
            var seg = ns.Contains('.') ? ns[(ns.LastIndexOf('.') + 1)..] : ns;
            name = seg + simple;
        }
        while (_usedNames.Contains(name)) name += "_";
        _usedNames.Add(name);
        return name;
    }

    private string Queue(Type t)
    {
        if (t.IsGenericType) t = t.GetGenericTypeDefinition();
        var owner = _shared != null && IsShared(t) ? _shared : this;
        if (owner != this) return owner.Queue(t);
        var key = t.FullName!;
        if (_names.TryGetValue(key, out var existing)) return existing;
        var name = t.Name.Contains('`') ? t.Name[..t.Name.IndexOf('`')] : t.Name;
        if (t.IsNested && t.DeclaringType != null) name = t.DeclaringType.Name + name;
        name = Unique(name, t.Namespace);
        _names[key] = name;
        _pending.Enqueue(t);
        return name;
    }

    // ---- emission ----

    private void EmitEnum(Type t, string name)
    {
        Doc(t, "");
        _out.AppendLine("declare enum " + name + " {");
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var raw = f.GetRawConstantValue();
            var value = raw == null ? "0" : Convert.ToString(raw, CultureInfo.InvariantCulture);
            _out.AppendLine("    " + Ident(f.Name) + " = " + value + ",");
        }
        _out.AppendLine("}");
    }

    private void EmitDelegate(Type t, string name)
    {
        var invoke = t.GetMethod("Invoke");
        if (invoke == null) { _out.AppendLine("type " + name + " = (...args: unknown[]) => void;"); return; }
        Doc(t, "");
        _out.AppendLine("type " + name + " = (" + Params(invoke.GetParameters()) + ") => " + Map(invoke.ReturnType) + ";");
    }

    private int EmitInterface(Type t, string name, bool isRoot)
    {
        var members = 0;
        Doc(t, "");
        var baseType = t.BaseType != null && IsOurs(t.BaseType) && t.BaseType.FullName != "System.Object" ? Map(t.BaseType) : null;
        _out.AppendLine("interface " + name + (baseType != null ? " extends " + baseType : "") + " {");
        var flags = BindingFlags.Public | BindingFlags.Instance | (baseType == null ? BindingFlags.FlattenHierarchy : BindingFlags.DeclaredOnly);
        if (isRoot) flags |= BindingFlags.DeclaredOnly;

        foreach (var f in t.GetFields(flags).Where(f => !f.IsSpecialName))
        {
            if (!Try(f.Name, () => (f.IsInitOnly || f.IsLiteral ? "readonly " : "") + Ident(f.Name) + ": " + Map(f.FieldType) + ";", f)) continue;
            members++;
        }
        foreach (var p in t.GetProperties(flags).Where(p => p.GetIndexParameters().Length == 0))
        {
            if (p.GetMethod?.IsPublic != true && p.SetMethod?.IsPublic != true) continue;
            if (!Try(p.Name, () => (p.SetMethod?.IsPublic == true ? "" : "readonly ") + Ident(p.Name) + ": " + Map(p.PropertyType) + ";", p)) continue;
            members++;
        }
        foreach (var e in t.GetEvents(flags))
        {
            if (!Try(e.Name, () => "readonly " + Ident(e.Name) + ": HostEvent<" + (e.EventHandlerType != null ? Map(e.EventHandlerType) : "(...args: unknown[]) => void") + ">;", e)) continue;
            members++;
        }
        foreach (var m in t.GetMethods(flags).Where(m => !m.IsSpecialName && !SkipMembers.Contains(m.Name) && !m.Name.Contains('<')))
        {
            if (m.DeclaringType?.FullName == "System.Object") continue;
            if (!Try(m.Name, () =>
                {
                    if (m.GetParameters().Any(p => p.ParameterType.IsPointer)) return null;
                    var generics = m.IsGenericMethodDefinition ? "<" + string.Join(", ", m.GetGenericArguments().Select(g => g.Name)) + ">" : "";
                    return Ident(m.Name) + generics + "(" + Params(m.GetParameters()) + "): " + Map(m.ReturnType) + ";";
                }, m)) continue;
            members++;
        }
        _out.AppendLine("}");
        return members;
    }

    /// <summary>Emits one member line; a member whose signature refers to an assembly the resolver cannot find is skipped with a comment.</summary>
    private bool Try(string name, Func<string?> render, MemberInfo member)
    {
        string? line;
        try
        {
            line = render();
        }
        catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException or BadImageFormatException)
        {
            _out.AppendLine("    // " + name + ": not emitted (" + ex.GetType().Name + ": " + ex.Message.Split('.')[0] + ")");
            Skipped++;
            return false;
        }
        if (line == null) return false;
        Doc(member, "    ");
        _out.AppendLine("    " + line);
        return true;
    }

    private string Params(ParameterInfo[] ps)
    {
        var parts = new List<string>();
        for (var i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            var isParams = i == ps.Length - 1 && p.ParameterType.IsArray && p.GetCustomAttributesData().Any(a => a.AttributeType.FullName == "System.ParamArrayAttribute");
            var pname = Ident(string.IsNullOrEmpty(p.Name) ? "arg" + i : p.Name);
            if (isParams) parts.Add("..." + pname + ": " + Map(p.ParameterType));
            else parts.Add(pname + (p.IsOptional || p.HasDefaultValue ? "?" : "") + ": " + Map(p.ParameterType));
        }
        return string.Join(", ", parts);
    }

    private void Doc(MemberInfo m, string indent)
    {
        var s = Docs.For(m);
        if (s == null) return;
        _out.AppendLine(indent + "/** " + s.Replace("*/", "* /") + " */");
    }

    private static string Ident(string name) => Reserved.Contains(name) ? name + "_" : name;
}

/// <summary>The machine-readable catalogue of the server API for the Bun bridge (T-006).</summary>
internal static class Catalogue
{
    public static string Build(Type api, Emitter emitter)
    {
        var functions = new List<object>();
        foreach (var m in api.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName && !m.Name.Contains('<')))
        {
            functions.Add(new
            {
                name = m.Name,
                parameters = m.GetParameters().Select(p => new { name = p.Name, type = p.ParameterType.FullName, ts = emitter.Map(p.ParameterType), optional = p.IsOptional || p.HasDefaultValue, isParams = p.ParameterType.IsArray && p.GetCustomAttributesData().Any(a => a.AttributeType.FullName == "System.ParamArrayAttribute") }).ToArray(),
                returns = m.ReturnType.FullName,
                returnsTs = emitter.Map(m.ReturnType),
                needsResult = m.ReturnType.FullName != "System.Void",
            });
        }
        var events = api.GetEvents(BindingFlags.Public | BindingFlags.Instance).Select(e =>
        {
            var invoke = e.EventHandlerType?.GetMethod("Invoke");
            return new { name = e.Name, parameters = invoke?.GetParameters().Select(p => new { name = p.Name, type = p.ParameterType.FullName, ts = emitter.Map(p.ParameterType) }).ToArray() };
        }).ToArray();
        var doc = new { generatedBy = "Tools/GTANetwork.TypeGen", assembly = api.Assembly.GetName().Name, version = api.Assembly.GetName().Version?.ToString(), functions, events };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
    }
}
