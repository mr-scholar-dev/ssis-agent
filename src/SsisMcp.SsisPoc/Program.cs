using System;
using System.IO;
using SsisMcp.Ssis;
using Dts = Microsoft.SqlServer.Dts.Runtime;

namespace SsisMcp.SsisPoc
{
    /// <summary>
    /// Phase 1 proof of concept. Proves the SSIS Object Model roundtrip on this runtime and,
    /// critically, that a TargetServerVersion=2016 package can be created, saved, reloaded and
    /// validated on the installed v17 runtime (resolving the versioning risk empirically).
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var target = args.Length > 0 ? args[0] : "2016";
            var outDir = Path.Combine(Path.GetTempPath(), "ssis-poc");
            var path = Path.Combine(outDir, $"PoC_{target}.dtsx");
            var svc = new PackageService();

            try
            {
                Step("Create in-memory package (target " + target + ")");
                var pkg = svc.CreateMinimalPackage("PoCPackage", target);
                Console.WriteLine($"   name={pkg.Name} target={svc.TryGetTargetServerVersion(pkg) ?? "(not exposed)"}");

                Step("Save to " + path);
                svc.Save(pkg, path);
                Console.WriteLine("   bytes=" + new FileInfo(path).Length);

                Step("Reload the saved copy");
                var reloaded = svc.Load(path);

                Step("Inspect reloaded package");
                var info = svc.Inspect(reloaded);
                Console.WriteLine($"   name={info.Name} target={info.TargetServerVersion} protection={info.ProtectionLevel}");
                Console.WriteLine("   executables:");
                foreach (var e in info.Executables)
                    Console.WriteLine($"     - {e.Name} [{e.TypeName}] ({e.CreationName})");
                Console.WriteLine("   connections:");
                foreach (var c in info.Connections)
                    Console.WriteLine($"     - {c.Name} ({c.CreationName})");

                Step("Validate (structural integrity)");
                var result = svc.Validate(reloaded);
                Console.WriteLine("   validate result = " + result);

                // Not-corrupt assertion: reloaded package must retain name, task and connection.
                bool FindRecursive(System.Collections.Generic.IEnumerable<SsisMcp.Core.Packages.ExecutableInfo> xs, string name)
                {
                    foreach (var x in xs)
                        if (x.Name == name || FindRecursive(x.Children, name)) return true;
                    return false;
                }
                var ok = info.Name == "PoCPackage"
                         && FindRecursive(info.Executables, "SqlBorrar")
                         && info.Connections.Exists(c => c.Name == "PracticaOrigen");

                var tgt = info.TargetServerVersion;
                Console.WriteLine();
                Console.WriteLine("TargetServerVersion finding: " +
                    (tgt == null
                        ? "runtime Package does NOT expose TargetServerVersion (project-level concept)."
                        : $"runtime persisted TargetServerVersion = '{tgt}' (requested {target})."));

                Console.WriteLine(ok
                    ? "PoC PASS: roundtrip preserved structure; package is not corrupt."
                    : "PoC FAIL: reloaded package lost expected structure.");
                return ok ? 0 : 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("PoC ERROR: " + ex.GetType().Name + ": " + ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        private static void Step(string s) => Console.WriteLine("[*] " + s);
    }
}
