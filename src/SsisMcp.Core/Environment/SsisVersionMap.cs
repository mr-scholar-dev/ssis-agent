using System.Collections.Generic;

namespace SsisMcp.Core.Environment
{
    /// <summary>
    /// Pure mapping between SSIS/DTS managed-assembly major versions, product years, and
    /// the <c>TargetServerVersion</c> values a runtime can typically emit.
    ///
    /// This is the seed of the version-adapter layer: the MCP core binds to whatever runtime
    /// major is installed in the GAC and, for downlevel package generation, relies on
    /// TargetServerVersion. Actual downlevel support MUST be confirmed by building a package
    /// (Phase 1) — this table only expresses the theoretical mapping.
    /// </summary>
    public static class SsisVersionMap
    {
        /// <summary>Maps a DTS managed-assembly major version to its SQL Server product year.</summary>
        public static string? ProductYearForAssemblyMajor(int major)
        {
            switch (major)
            {
                case 11: return "2012";
                case 12: return "2014";
                case 13: return "2016";
                case 14: return "2017";
                case 15: return "2019";
                case 16: return "2022";
                case 17: return "2025";
                default: return null;
            }
        }

        /// <summary>
        /// TargetServerVersion values a runtime of the given major can, in principle, emit.
        /// A newer runtime can target its own year and older ones; it cannot target the future.
        /// </summary>
        public static IReadOnlyList<string> TargetableYearsForRuntimeMajor(int major)
        {
            var all = new[] { "2016", "2017", "2019", "2022", "2025" };
            var result = new List<string>();
            var product = ProductYearForAssemblyMajor(major);
            foreach (var year in all)
            {
                result.Add(year);
                if (year == product) break;
            }
            return result;
        }
    }
}
