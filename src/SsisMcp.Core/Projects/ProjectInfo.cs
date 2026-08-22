using System.Collections.Generic;
using SsisMcp.Core.Packages;

namespace SsisMcp.Core.Projects
{
    /// <summary>Structured snapshot of a .dtproj project. Backs <c>project.inspect</c>.</summary>
    public sealed class ProjectInfo
    {
        public string Name { get; set; } = "";
        public string ProjectPath { get; set; } = "";

        /// <summary>"Project" (project deployment) or "Package" (legacy package deployment).</summary>
        public string? DeploymentModel { get; set; }

        /// <summary>TargetServerVersion declared in the .dtproj, e.g. "SQLServer2016".</summary>
        public string? TargetServerVersion { get; set; }

        public string? ProtectionLevel { get; set; }

        /// <summary>Packages referenced by the project (name + whether the .dtsx file exists).</summary>
        public List<ProjectPackageRef> Packages { get; } = new List<ProjectPackageRef>();

        /// <summary>Project-level connection managers (.conmgr), by name.</summary>
        public List<ConnectionInfo> ProjectConnectionManagers { get; } = new List<ConnectionInfo>();

        /// <summary>Environment / compatibility assessment for agents (see fields below).</summary>
        public ProjectDiagnostics Diagnostics { get; set; } = new ProjectDiagnostics();
    }

    public sealed class ProjectPackageRef
    {
        public string Name { get; set; } = "";
        public string? Path { get; set; }
        public bool FileExists { get; set; }
        public int? PackageFormatVersion { get; set; }
    }

    /// <summary>
    /// Explicit compatibility assessment. Deliberately conservative: a target is only reported as
    /// "verified" when it has actually been proven with a real .dtproj build against that target.
    /// </summary>
    public sealed class ProjectDiagnostics
    {
        /// <summary>SSIS runtime detected in the environment, e.g. "ManagedDTS v17.0.0.0 (SSIS 2025)".</summary>
        public string? DetectedSsisRuntime { get; set; }

        /// <summary>Visual Studio generations considered compatible for this project.</summary>
        public List<string> CompatibleVisualStudio { get; } = new List<string>();

        /// <summary>The VS generation recommended for opening this project.</summary>
        public string? RecommendedVisualStudio { get; set; }

        /// <summary>The project's TargetServerVersion echoed for convenience.</summary>
        public string? TargetServerVersion { get; set; }

        /// <summary>True only when the target has been proven with a real build (never assumed).</summary>
        public bool TargetServerVersionVerified { get; set; }

        /// <summary>Known incompatibilities or caveats the agent must weigh before modifying.</summary>
        public List<string> KnownIncompatibilities { get; } = new List<string>();

        /// <summary>Distinct PackageFormatVersions found across the project's packages.</summary>
        public List<int> PackageFormatVersions { get; } = new List<int>();
    }
}
