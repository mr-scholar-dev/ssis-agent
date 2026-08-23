using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SsisMcp.Core.Ide;

namespace SsisMcp.VisualStudioBridge
{
    /// <summary>
    /// Detects all installed Visual Studio instances via vswhere and classifies each for SSIS work.
    /// Never assumes "VS installed" implies "SSIS Designer available" — Build Tools has no IDE, and a
    /// full IDE without the SSIS Projects extension cannot open .dtproj (reported DesignerUnavailable).
    /// </summary>
    public sealed class WindowsVisualStudioLocator : IVisualStudioLocator
    {
        private readonly Func<IReadOnlyList<VsWhereRecord>> _vswhere;
        private readonly Func<string, bool> _hasIde;
        private readonly Func<string, bool> _hasSsisDesigner;

        public WindowsVisualStudioLocator(
            Func<IReadOnlyList<VsWhereRecord>>? vswhere = null,
            Func<string, bool>? hasIde = null,
            Func<string, bool>? hasSsisDesigner = null)
        {
            _vswhere = vswhere ?? RunVsWhere;
            _hasIde = hasIde ?? DefaultHasIde;
            _hasSsisDesigner = hasSsisDesigner ?? DefaultHasSsisDesigner;
        }

        public IReadOnlyList<VisualStudioInstanceInfo> DetectAll()
        {
            var list = new List<VisualStudioInstanceInfo>();
            foreach (var rec in _vswhere())
                list.Add(VsInstanceClassifier.Classify(rec, _hasIde(rec.InstallationPath), _hasSsisDesigner(rec.InstallationPath)));
            return list;
        }

        public VisualStudioInstanceInfo? Select(TargetIde target)
        {
            var all = DetectAll();
            if (target == TargetIde.VS2022) return all.FirstOrDefault(i => i.Generation == TargetIde.VS2022);
            if (target == TargetIde.VS2026) return all.FirstOrDefault(i => i.Generation == TargetIde.VS2026);
            // Auto: prefer an instance that can open .dtproj; among those, the newest generation;
            // otherwise the newest instance overall. Absence is returned as null, never guessed.
            var usable = all.Where(i => i.CanOpenDtproj).ToList();
            var pool = usable.Count > 0 ? usable : all.ToList();
            return pool.OrderByDescending(i => i.Generation).ThenByDescending(i => i.Version, StringComparer.Ordinal).FirstOrDefault();
        }

        // --- real probes ---

        private static IReadOnlyList<VsWhereRecord> RunVsWhere()
        {
            var vswhere = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Microsoft Visual Studio\Installer\vswhere.exe");
            var records = new List<VsWhereRecord>();
            if (!File.Exists(vswhere)) return records;

            var paths = ReadProperty(vswhere, "installationPath");
            var versions = ReadProperty(vswhere, "catalog_productDisplayVersion");
            var products = ReadProperty(vswhere, "productId");
            for (var i = 0; i < paths.Count; i++)
                records.Add(new VsWhereRecord
                {
                    InstallationPath = paths[i],
                    DisplayVersion = i < versions.Count ? versions[i] : "",
                    ProductId = i < products.Count ? products[i] : ""
                });
            return records;
        }

        private static List<string> ReadProperty(string vswhere, string property)
        {
            var psi = new ProcessStartInfo(vswhere, $"-all -prerelease -products * -property {property}")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
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

        private static bool DefaultHasIde(string installationPath) =>
            File.Exists(Path.Combine(installationPath, @"Common7\IDE\devenv.exe"));

        private static bool DefaultHasSsisDesigner(string installationPath)
        {
            // The SSIS Projects extension installs the designer under CommonExtensions\Microsoft\SSIS
            // (not the per-user Extensions folder). Check both, plus the per-user extension store.
            var candidates = new[]
            {
                Path.Combine(installationPath, @"Common7\IDE\CommonExtensions\Microsoft\SSIS"),
                Path.Combine(installationPath, @"Common7\IDE\CommonExtensions"),
                Path.Combine(installationPath, @"Common7\IDE\Extensions"),
            };
            foreach (var dir in candidates)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    if (Directory.EnumerateFiles(dir, "Microsoft.DataTransformationServices.Design.dll", SearchOption.AllDirectories).Any())
                        return true;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return false;
        }
    }

    /// <summary>Adapter for the VS 2022 (v17) generation. Contains no SSIS logic.</summary>
    public sealed class VisualStudio2022Adapter : IVisualStudioAdapter
    {
        public TargetIde Generation => TargetIde.VS2022;
        public bool Supports(VisualStudioInstanceInfo instance) => instance.Generation == TargetIde.VS2022;
    }

    /// <summary>Adapter for the VS 2026 (v18) generation. Contains no SSIS logic.</summary>
    public sealed class VisualStudio2026Adapter : IVisualStudioAdapter
    {
        public TargetIde Generation => TargetIde.VS2026;
        public bool Supports(VisualStudioInstanceInfo instance) => instance.Generation == TargetIde.VS2026;
    }
}
