using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GTANetworkServer.Constant;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;

namespace GTANetworkServer.Managers
{
    /// <summary>
    /// Compiles C# / Visual Basic resource scripts in memory with Roslyn.
    /// (The old implementation used System.CodeDom, which can only compile on Windows/.NET Framework.)
    /// </summary>
    internal static class ScriptCompiler
    {
        private static readonly object ReferenceLock = new object();
        private static List<MetadataReference> _platformReferences;

        /// <summary>
        /// Compiles the given sources into an in-memory assembly. Returns null when compilation failed
        /// (errors are reported through <paramref name="log"/>, warnings do not prevent loading).
        /// </summary>
        public static Assembly Compile(IList<string> sources, IEnumerable<string> extraReferences, bool visualBasic, Action<string, LogCat> log)
        {
            var references = new List<MetadataReference>(GetPlatformReferences());

            foreach (var reference in extraReferences ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(reference)) continue;

                var path = ResolveReference(reference);
                if (path == null)
                {
                    log("WARN: Referenced assembly \"" + reference + "\" was not found, the script may fail to compile.", LogCat.Warn);
                    continue;
                }

                references.Add(MetadataReference.CreateFromFile(path));
            }

            var assemblyName = "GTANResource_" + Guid.NewGuid().ToString("N");
            Compilation compilation;

            if (!visualBasic)
            {
                var parseOptions = new CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest);
                var trees = sources.Select((text, i) => CSharpSyntaxTree.ParseText(text, parseOptions, "script" + i + ".cs")).ToList();
                var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release, allowUnsafe: true, checkOverflow: false);

                compilation = CSharpCompilation.Create(assemblyName, trees, references, options);
            }
            else
            {
                var parseOptions = new VisualBasicParseOptions(Microsoft.CodeAnalysis.VisualBasic.LanguageVersion.Latest);
                var trees = sources.Select((text, i) => VisualBasicSyntaxTree.ParseText(text, parseOptions, "script" + i + ".vb")).ToList();
                var options = new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release);

                compilation = VisualBasicCompilation.Create(assemblyName, trees, references, options);
            }

            using (var stream = new MemoryStream())
            {
                var result = compilation.Emit(stream);

                var diagnostics = result.Diagnostics
                    .Where(d => !d.IsSuppressed && d.Severity >= DiagnosticSeverity.Warning)
                    .ToList();

                if (diagnostics.Count > 0)
                {
                    log("Error/warning while compiling script!", LogCat.Warn);

                    foreach (var diagnostic in diagnostics)
                    {
                        var span = diagnostic.Location.GetLineSpan();
                        var isWarning = diagnostic.Severity == DiagnosticSeverity.Warning;

                        log(string.Format("{0} ({1}) at {2}:{3}: {4}",
                                isWarning ? "Warning" : "Error",
                                diagnostic.Id,
                                string.IsNullOrEmpty(span.Path) ? "script" : span.Path,
                                span.StartLinePosition.Line + 1,
                                diagnostic.GetMessage()),
                            isWarning ? LogCat.Warn : LogCat.Error);
                    }
                }

                if (!result.Success) return null;

                return Assembly.Load(stream.ToArray());
            }
        }

        private static string ResolveReference(string reference)
        {
            var candidates = new[]
            {
                reference,
                Path.Combine(AppContext.BaseDirectory, reference),
                Path.Combine(AppContext.BaseDirectory, reference + ".dll"),
                Path.Combine(AppContext.BaseDirectory, Path.GetFileName(reference)),
            };

            return candidates.FirstOrDefault(File.Exists) is string found ? Path.GetFullPath(found) : null;
        }

        /// <summary>
        /// The runtime's own assemblies plus everything the server already has loaded
        /// (GTANetworkServer, GTANetworkShared, Lidgren, Newtonsoft.Json, protobuf-net ...).
        /// </summary>
        private static IEnumerable<MetadataReference> GetPlatformReferences()
        {
            lock (ReferenceLock)
            {
                if (_platformReferences != null) return _platformReferences;

                var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
                if (!string.IsNullOrEmpty(trustedAssemblies))
                {
                    foreach (var path in trustedAssemblies.Split(Path.PathSeparator))
                    {
                        if (File.Exists(path)) byName[Path.GetFileNameWithoutExtension(path)] = path;
                    }
                }

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    AddAssembly(byName, assembly);
                }

                // Make sure the API surface a script needs is always referenced, even if not loaded yet.
                AddAssembly(byName, typeof(ScriptCompiler).Assembly);
                AddAssembly(byName, typeof(GTANetworkShared.Vector3).Assembly);
                AddAssembly(byName, typeof(Lidgren.Network.NetPeer).Assembly);
                AddAssembly(byName, typeof(Newtonsoft.Json.JsonConvert).Assembly);
                AddAssembly(byName, typeof(ProtoBuf.Serializer).Assembly);

                _platformReferences = byName.Values
                    .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                    .ToList();

                return _platformReferences;
            }
        }

        private static void AddAssembly(IDictionary<string, string> byName, Assembly assembly)
        {
            if (assembly == null || assembly.IsDynamic) return;

            string location;
            try
            {
                location = assembly.Location;
            }
            catch
            {
                return;
            }

            if (string.IsNullOrEmpty(location) || !File.Exists(location)) return;

            byName[Path.GetFileNameWithoutExtension(location)] = location;
        }
    }
}
