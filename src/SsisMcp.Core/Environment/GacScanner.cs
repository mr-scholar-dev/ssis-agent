using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SsisMcp.Core.Environment
{
    /// <summary>
    /// Finds assembly versions in the .NET Framework GAC by parsing the version-stamped
    /// folder names (e.g. <c>v4.0_17.0.0.0__89845dcd8080cc91</c>). We avoid Assembly.Load so
    /// the detector runs even when SSIS is absent, and so it can report multiple installed majors.
    /// </summary>
    public static class GacScanner
    {
        // Matches ".._<major>.<minor>.<build>.<rev>__<token>"
        private static readonly Regex VersionFolder =
            new Regex(@"_(?<ver>\d+\.\d+\.\d+\.\d+)__", RegexOptions.Compiled);

        /// <summary>The GAC roots on a 64-bit Windows install.</summary>
        public static IEnumerable<string> DefaultGacRoots()
        {
            var win = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
            var root = Path.Combine(win, "Microsoft.NET", "assembly");
            foreach (var sub in new[] { "GAC_MSIL", "GAC_64", "GAC_32" })
                yield return Path.Combine(root, sub);
        }

        /// <summary>
        /// Returns the distinct <see cref="Version"/>s of <paramref name="assemblyName"/> found
        /// under the given GAC roots. Empty when the assembly is not installed.
        /// </summary>
        public static IReadOnlyList<Version> FindVersions(string assemblyName, IEnumerable<string> gacRoots)
        {
            var found = new HashSet<Version>();
            foreach (var gacRoot in gacRoots)
            {
                var asmDir = Path.Combine(gacRoot, assemblyName);
                if (!Directory.Exists(asmDir)) continue;
                foreach (var verDir in Directory.EnumerateDirectories(asmDir))
                {
                    var m = VersionFolder.Match(Path.GetFileName(verDir) + "__");
                    if (m.Success && Version.TryParse(m.Groups["ver"].Value, out var v))
                        found.Add(v);
                }
            }
            return found.OrderByDescending(v => v).ToList();
        }
    }
}
