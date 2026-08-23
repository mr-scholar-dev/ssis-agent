using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SsisMcp.Ssis
{
    /// <summary>
    /// Some SSIS managed pipeline components (notably the v17 ADO NET Source/Destination) depend at
    /// design time on assemblies that ship with SSDT but are not in the GAC or our probing path —
    /// e.g. Microsoft.Data.SqlClient 5.x and its dependencies under
    /// <c>…\CommonExtensions\Microsoft\SSIS\&lt;ver&gt;\Extensions\SQLCommon</c>. This resolver makes those
    /// discoverable (managed via AssemblyResolve, native SNI via the DLL search path). Idempotent.
    /// </summary>
    public static class SsisAssemblyResolver
    {
        private static readonly object Gate = new object();
        private static bool _installed;
        private static string[] _dirs = Array.Empty<string>();
        private static readonly Dictionary<string, Assembly> Cache = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Diagnostics: last error encountered while pre-loading (for probes/tests).</summary>
        public static string? LastError { get; private set; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        /// <summary>Installs the resolver (safe to call repeatedly). Returns the probe directories used.</summary>
        public static IReadOnlyList<string> Install()
        {
            lock (Gate)
            {
                if (_installed) return _dirs;
                _dirs = DiscoverSqlCommonDirs().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (_dirs.Length > 0)
                {
                    SetDllDirectory(_dirs[0]); // native SNI (Microsoft.Data.SqlClient.SNI.x64.dll)
                    AppDomain.CurrentDomain.AssemblyResolve += Resolve;
                    // Eagerly load every managed dll in the SQLCommon folder into a cache so that a
                    // later Assembly.Load(fullName) is satisfied from here (LoadFrom-context assemblies
                    // are not visible to the default Load context otherwise).
                    foreach (var dir in _dirs)
                        foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
                        {
                            try
                            {
                                var name = AssemblyName.GetAssemblyName(dll).Name;
                                if (name != null && !Cache.ContainsKey(name))
                                    Cache[name] = Assembly.LoadFrom(dll);
                            }
                            catch (BadImageFormatException) { /* native dll (SNI) — skip */ }
                            catch (Exception ex) { LastError = Path.GetFileName(dll) + ": " + ex.GetType().Name + ": " + ex.Message; }
                        }
                }
                _installed = true;
                return _dirs;
            }
        }

        private static Assembly? Resolve(object? sender, ResolveEventArgs args)
        {
            var simpleName = new AssemblyName(args.Name).Name;
            if (string.IsNullOrEmpty(simpleName)) return null;
            if (Cache.TryGetValue(simpleName!, out var cached)) return cached;
            foreach (var dir in _dirs)
            {
                var candidate = Path.Combine(dir, simpleName + ".dll");
                if (File.Exists(candidate))
                {
                    try { return Assembly.LoadFrom(candidate); }
                    catch (BadImageFormatException) { }
                    catch (FileLoadException) { }
                }
            }
            return null;
        }

        private static IEnumerable<string> DiscoverSqlCommonDirs()
        {
            var roots = new[]
            {
                @"C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\CommonExtensions\Microsoft\SSIS",
                @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\CommonExtensions\Microsoft\SSIS",
                @"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\SSIS",
            };
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var ver in Directory.EnumerateDirectories(root)) // e.g. \170
                {
                    var sqlCommon = Path.Combine(ver, @"Extensions\SQLCommon");
                    if (Directory.Exists(sqlCommon)) yield return sqlCommon;
                }
            }
        }
    }
}
