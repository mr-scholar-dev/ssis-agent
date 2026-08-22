using System;
using System.Linq;
using System.Text.RegularExpressions;
using SsisMcp.Core.Environment;
using SsisMcp.Core.Projects;

namespace SsisMcp.Ssis.Inspection
{
    /// <summary>
    /// Builds a <see cref="ProjectInfo"/> with an explicit compatibility assessment for agents:
    /// detected SSIS runtime, compatible/recommended Visual Studio, TargetServerVersion, package
    /// format versions, and known incompatibilities. Conservative by design — a target is never
    /// reported as verified unless proven by a real .dtproj build.
    /// </summary>
    public sealed class ProjectInspector
    {
        private readonly IEnvironmentDetector _environment;
        private readonly PackageService _packages;

        public ProjectInspector(IEnvironmentDetector? environment = null, PackageService? packages = null)
        {
            _environment = environment ?? new WindowsEnvironmentDetector();
            _packages = packages ?? new PackageService();
        }

        public ProjectInfo Inspect(string dtprojPath)
        {
            var info = DtprojReader.Read(dtprojPath);
            var env = _environment.Detect();
            var diag = info.Diagnostics;

            diag.TargetServerVersion = info.TargetServerVersion;

            var runtime = env.Find("ssis.runtime");
            diag.DetectedSsisRuntime = runtime?.Detail;

            // Official IDE targets (both first-class). VS-adapter phase will refine per-instance.
            diag.CompatibleVisualStudio.Add("Visual Studio 2022");
            diag.CompatibleVisualStudio.Add("Visual Studio 2026");
            var vs = env.Find("visualstudio")?.Detail ?? "";
            diag.RecommendedVisualStudio =
                vs.Contains("2026") ? "Visual Studio 2026"
                : vs.Contains("2022") ? "Visual Studio 2022"
                : "Visual Studio 2022 or 2026";

            // Package format versions across the project's packages.
            foreach (var pkg in info.Packages.Where(p => p.FileExists && p.Path != null))
            {
                var fmt = PackageFormatVersionReader.FromFile(pkg.Path!);
                pkg.PackageFormatVersion = fmt;
                if (fmt.HasValue && !diag.PackageFormatVersions.Contains(fmt.Value))
                    diag.PackageFormatVersions.Add(fmt.Value);
            }

            AssessTargetCompatibility(info, env, diag);

            if (env.Find("ssis.projects.extension")?.Status != CheckStatus.Pass)
                diag.KnownIncompatibilities.Add(
                    "SSIS Projects extension not detected in any Visual Studio instance: in-VS design-time " +
                    "editing is unavailable (the programmatic SSIS API still works).");

            return info;
        }

        private static void AssessTargetCompatibility(ProjectInfo info, EnvironmentReport env, ProjectDiagnostics diag)
        {
            var targetYear = ExtractYear(info.TargetServerVersion);
            var runtimeMajor = RuntimeMajor(env);

            if (targetYear == null || runtimeMajor == null)
            {
                diag.TargetServerVersionVerified = false;
                return;
            }

            var targetable = SsisVersionMap.TargetableYearsForRuntimeMajor(runtimeMajor.Value);
            var runtimeYear = SsisVersionMap.ProductYearForAssemblyMajor(runtimeMajor.Value);

            if (!targetable.Contains(targetYear))
            {
                diag.KnownIncompatibilities.Add(
                    $"Project targets SQL Server {targetYear}, but the installed SSIS runtime (SSIS {runtimeYear}) " +
                    $"can only emit: {string.Join("/", targetable)}.");
            }
            else if (targetYear != runtimeYear)
            {
                diag.KnownIncompatibilities.Add(
                    $"Project targets SQL Server {targetYear} while the installed runtime is SSIS {runtimeYear}. " +
                    $"Downlevel targeting is theoretically supported via TargetServerVersion at project build, " +
                    $"but has NOT been verified with a real {targetYear} build on this machine.");
            }

            // Never claim verification without a proven real build of this exact target.
            diag.TargetServerVersionVerified = false;
        }

        private static string? ExtractYear(string? targetServerVersion)
        {
            if (string.IsNullOrEmpty(targetServerVersion)) return null;
            var m = Regex.Match(targetServerVersion!, @"(20\d{2})");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static int? RuntimeMajor(EnvironmentReport env)
        {
            var v = env.Find("ssis.runtime")?.Data;
            if (v != null && v.TryGetValue("managedDtsVersion", out var ver) &&
                Version.TryParse(ver, out var parsed))
                return parsed.Major;
            return null;
        }
    }
}
