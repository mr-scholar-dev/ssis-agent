using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace SsisMcp.Core.Environment
{
    /// <summary>
    /// Concrete Windows probe. Every check is defensive: a probe that throws becomes a
    /// <see cref="CheckStatus.Unknown"/> result rather than crashing the whole report.
    /// </summary>
    public sealed class WindowsEnvironmentDetector : IEnvironmentDetector
    {
        public EnvironmentReport Detect()
        {
            var report = new EnvironmentReport();
            report.Checks.Add(Safe("os", CheckOs));
            report.Checks.Add(Safe("dotnet.framework", CheckNetFramework));
            report.Checks.Add(Safe("visualstudio", CheckVisualStudio));
            report.Checks.Add(Safe("ssis.projects.extension", CheckSsisProjectsExtension));
            report.Checks.Add(Safe("ssis.runtime", CheckSsisRuntime, critical: true));
            report.Checks.Add(Safe("architecture", CheckArchitecture));
            report.Checks.Add(Safe("provider.sqlserver", CheckSqlProvider));
            report.Checks.Add(Safe("provider.ace.excel_access", CheckAceProvider));
            report.Checks.Add(Safe("sqlserver.connectivity", CheckSqlConnectivity));
            return report;
        }

        private static CheckResult Safe(string name, Func<CheckResult> probe, bool critical = false)
        {
            try
            {
                var r = probe();
                return r;
            }
            catch (Exception ex)
            {
                return new CheckResult(name, CheckStatus.Unknown, "probe error: " + ex.Message, critical);
            }
        }

        private CheckResult CheckOs()
        {
            var name = TryReadRegistry(RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName") ?? "Windows";
            var build = System.Environment.OSVersion.Version.Build;
            // Windows 11 reports build >= 22000, but the registry ProductName still says
            // "Windows 10 ..." on Win11 (a documented Microsoft quirk) — correct the label.
            if (build >= 22000 && name.Contains("Windows 10"))
                name = name.Replace("Windows 10", "Windows 11");
            var status = build >= 10240 ? CheckStatus.Pass : CheckStatus.Warn;
            var r = new CheckResult("os", status, $"{name} (build {build})");
            r.Data["build"] = build.ToString();
            return r;
        }

        private CheckResult CheckNetFramework()
        {
            var release = TryReadRegistry(RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release");
            // 528040 == .NET Framework 4.8; 533320 == 4.8.1
            var hasNet48 = int.TryParse(release, out var rel) && rel >= 528040;
            var r = new CheckResult("dotnet.framework",
                hasNet48 ? CheckStatus.Pass : CheckStatus.Warn,
                hasNet48 ? ".NET Framework 4.8+ present" : "no .NET Framework 4.8 detected (release=" + (release ?? "?") + ")");
            if (release != null) r.Data["release"] = release;
            return r;
        }

        private CheckResult CheckVisualStudio()
        {
            var vswhere = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86),
                @"Microsoft Visual Studio\Installer\vswhere.exe");
            if (!File.Exists(vswhere))
                return new CheckResult("visualstudio", CheckStatus.Warn, "vswhere.exe not found; VS may not be installed");

            var (paths, versions) = RunVsWhere(vswhere);
            if (paths.Count == 0)
                return new CheckResult("visualstudio", CheckStatus.Warn, "no VS instances reported by vswhere");

            var mapped = versions.Select(v =>
            {
                var major = v.Split('.').FirstOrDefault();
                var year = major == "17" ? "2022" : major == "18" ? "2026" : major;
                return $"{year} (v{v})";
            });
            var r = new CheckResult("visualstudio", CheckStatus.Pass, string.Join(", ", mapped));
            for (var i = 0; i < paths.Count; i++) r.Data["path[" + i + "]"] = paths[i];
            return r;
        }

        private CheckResult CheckSsisProjectsExtension()
        {
            // The SSIS Object Model does NOT require the VS design-time extension, but the
            // extension is what enables editing packages inside VS. Report it separately.
            var vswhere = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86),
                @"Microsoft Visual Studio\Installer\vswhere.exe");
            if (!File.Exists(vswhere))
                return new CheckResult("ssis.projects.extension", CheckStatus.Unknown, "cannot locate VS to inspect extensions");

            var (paths, _) = RunVsWhere(vswhere);
            foreach (var p in paths)
            {
                var extDir = Path.Combine(p, @"Common7\IDE\Extensions");
                if (!Directory.Exists(extDir)) continue;
                var hit = Directory.EnumerateFiles(extDir, "*IntegrationServices*", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(extDir, "Microsoft.DataTools.Integration*", SearchOption.AllDirectories))
                    .Any();
                if (hit)
                    return new CheckResult("ssis.projects.extension", CheckStatus.Pass, "found under " + p);
            }
            return new CheckResult("ssis.projects.extension", CheckStatus.Warn,
                "SSIS Projects extension not detected in any VS instance (design-time editing unavailable; programmatic API still works)");
        }

        private CheckResult CheckSsisRuntime()
        {
            var roots = GacScanner.DefaultGacRoots().ToList();
            var managed = GacScanner.FindVersions("Microsoft.SqlServer.ManagedDTS", roots);
            var pipeWrap = GacScanner.FindVersions("Microsoft.SqlServer.DTSPipelineWrap", roots);

            if (managed.Count == 0)
                return new CheckResult("ssis.runtime", CheckStatus.Fail,
                    "Microsoft.SqlServer.ManagedDTS not found in GAC — SSIS runtime not installed", critical: true);

            var top = managed[0];
            var year = SsisVersionMap.ProductYearForAssemblyMajor(top.Major) ?? "unknown";
            var targetable = string.Join("/", SsisVersionMap.TargetableYearsForRuntimeMajor(top.Major));
            var r = new CheckResult("ssis.runtime", CheckStatus.Pass,
                $"ManagedDTS v{top} (SSIS {year}); can target: {targetable}", critical: true);
            r.Data["managedDtsVersion"] = top.ToString();
            r.Data["productYear"] = year;
            r.Data["allManagedVersions"] = string.Join(",", managed.Select(v => v.ToString()));
            r.Data["pipelineWrapVersions"] = string.Join(",", pipeWrap.Select(v => v.ToString()));
            return r;
        }

        private CheckResult CheckArchitecture()
        {
            var roots = GacScanner.DefaultGacRoots().ToList();
            var wrap64 = GacScanner.FindVersions("Microsoft.SqlServer.DTSRuntimeWrap",
                roots.Where(r => r.EndsWith("GAC_64", StringComparison.OrdinalIgnoreCase)));
            var arch = wrap64.Count > 0 ? "x64" : "unknown";
            var r = new CheckResult("architecture", CheckStatus.Pass,
                $"DTS runtime is {arch}; SSIS host processes must match this bitness");
            r.Data["ssisArch"] = arch;
            r.Data["processIs64Bit"] = System.Environment.Is64BitProcess.ToString();
            return r;
        }

        private CheckResult CheckSqlProvider()
        {
            var providers = EnumerateOleDbProviders();
            var sql = providers.Where(p => p.StartsWith("MSOLEDBSQL", StringComparison.OrdinalIgnoreCase)
                                        || p.Equals("SQLOLEDB", StringComparison.OrdinalIgnoreCase)).ToList();
            var status = sql.Count > 0 ? CheckStatus.Pass : CheckStatus.Warn;
            return new CheckResult("provider.sqlserver", status,
                sql.Count > 0 ? string.Join(", ", sql) : "no SQL Server OLE DB provider found");
        }

        private CheckResult CheckAceProvider()
        {
            var providers = EnumerateOleDbProviders();
            var ace = providers.Where(p => p.StartsWith("Microsoft.ACE.OLEDB", StringComparison.OrdinalIgnoreCase)).ToList();
            if (ace.Count == 0)
                return new CheckResult("provider.ace.excel_access", CheckStatus.Fail,
                    "no ACE OLE DB provider — Excel/Access sources unavailable");
            var r = new CheckResult("provider.ace.excel_access", CheckStatus.Pass, string.Join(", ", ace));
            r.Data["providers"] = string.Join(",", ace);
            return r;
        }

        private CheckResult CheckSqlConnectivity()
        {
            // Best-effort connect to the local default instance with integrated security.
            // Non-critical: absence just means SQL tools cannot be exercised here.
            var csb = new SqlConnectionStringBuilder
            {
                DataSource = ".",
                InitialCatalog = "master",
                IntegratedSecurity = true,
                ConnectTimeout = 3,
                TrustServerCertificate = true
            };
            try
            {
                using (var conn = new SqlConnection(csb.ConnectionString))
                {
                    conn.Open();
                    var ver = conn.ServerVersion;
                    return new CheckResult("sqlserver.connectivity", CheckStatus.Pass,
                        $"connected to local default instance (server version {ver})");
                }
            }
            catch (Exception ex)
            {
                return new CheckResult("sqlserver.connectivity", CheckStatus.Warn,
                    "could not connect to local default instance: " + ex.Message);
            }
        }

        // --- helpers ---

        private static (List<string> paths, List<string> versions) RunVsWhere(string vswhere)
        {
            var paths = ReadVsWhere(vswhere, "installationPath");
            var versions = ReadVsWhere(vswhere, "catalog_productDisplayVersion");
            return (paths, versions);
        }

        private static List<string> ReadVsWhere(string vswhere, string property)
        {
            var psi = new ProcessStartInfo(vswhere,
                $"-all -prerelease -products * -property {property}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var lines = new List<string>();
            using (var p = Process.Start(psi))
            {
                if (p == null) return lines;
                string? line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                    if (!string.IsNullOrWhiteSpace(line)) lines.Add(line.Trim());
                p.WaitForExit(10000);
            }
            return lines;
        }

        private static List<string> EnumerateOleDbProviders()
        {
            var list = new List<string>();
            using (var table = new OleDbEnumerator().GetElements())
            {
                foreach (System.Data.DataRow row in table.Rows)
                {
                    var name = row["SOURCES_NAME"] as string;
                    if (!string.IsNullOrWhiteSpace(name)) list.Add(name!);
                }
            }
            return list;
        }

        private static string? TryReadRegistry(RegistryHive hive, string subKey, string value)
        {
            using (var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
            using (var key = baseKey.OpenSubKey(subKey))
            {
                return key?.GetValue(value)?.ToString();
            }
        }
    }
}
