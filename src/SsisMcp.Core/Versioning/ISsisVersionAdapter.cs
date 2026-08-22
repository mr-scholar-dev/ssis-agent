using System.Collections.Generic;

namespace SsisMcp.Core.Versioning
{
    /// <summary>
    /// Design contract for adapting to a specific SSIS runtime generation (2016/2017/2019/2022/2025).
    /// This is a DIFFERENT responsibility from IVisualStudioAdapter: it concerns the SSIS runtime
    /// assemblies and TargetServerVersion, not the IDE. A given VS version does not imply a given
    /// SSIS target — the two adapters are resolved independently.
    /// </summary>
    public interface ISsisVersionAdapter
    {
        /// <summary>Product year of the bound runtime, e.g. "2025".</summary>
        string ProductYear { get; }

        /// <summary>DTS managed-assembly major this adapter binds to, e.g. 17.</summary>
        int AssemblyMajor { get; }

        /// <summary>TargetServerVersion years this runtime can emit (project-build level).</summary>
        IReadOnlyList<string> TargetableYears { get; }

        /// <summary>True if this adapter can, in principle, emit a package for the given year.</summary>
        bool CanTarget(string year);
    }
}
