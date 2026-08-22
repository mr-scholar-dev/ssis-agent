using System.Collections.Generic;

namespace SsisMcp.Core.Ide
{
    /// <summary>Which IDE an operation targets. <see cref="Auto"/> lets the selector choose.</summary>
    public enum TargetIde
    {
        Auto,
        VS2022,
        VS2026
    }

    /// <summary>
    /// Design contract (implementation scheduled after the Data Flow builder, per project order).
    /// Enumerated facts about one installed Visual Studio instance. Detection MUST enumerate ALL
    /// instances and never assume the active one — see docs/visual-studio-adapters.md.
    /// </summary>
    public sealed class VisualStudioInstanceInfo
    {
        /// <summary>e.g. "17.14.35" or "18.7.3".</summary>
        public string Version { get; set; } = "";

        /// <summary>Mapped generation for explicit targeting.</summary>
        public TargetIde Generation { get; set; } = TargetIde.Auto;

        /// <summary>e.g. "Community", "Professional", "Enterprise".</summary>
        public string? Edition { get; set; }

        public string InstallationPath { get; set; } = "";

        /// <summary>Whether the SQL Server Integration Services Projects extension is installed.</summary>
        public bool SsisProjectsExtensionInstalled { get; set; }

        /// <summary>Version of the SSIS Projects extension, when detectable.</summary>
        public string? SsisProjectsExtensionVersion { get; set; }

        /// <summary>Can this instance open .dtproj projects (i.e. extension present & compatible)?</summary>
        public bool CanOpenDtproj { get; set; }

        /// <summary>Is a VSIX/bridge compatible with this instance available?</summary>
        public bool BridgeCompatible { get; set; }

        /// <summary>Free-form capability flags for forward compatibility.</summary>
        public List<string> Capabilities { get; } = new List<string>();
    }

    /// <summary>
    /// Design contract for a Visual Studio version adapter. One concrete implementation per
    /// generation (VisualStudio2022Adapter, VisualStudio2026Adapter). The SSIS MCP core never
    /// talks to VS directly — only through this interface — and works with NO VS installed.
    /// </summary>
    public interface IVisualStudioAdapter
    {
        /// <summary>The generation this adapter serves.</summary>
        TargetIde Generation { get; }

        /// <summary>True if this adapter can drive the given detected instance.</summary>
        bool Supports(VisualStudioInstanceInfo instance);
    }

    /// <summary>
    /// Design contract for detecting/selecting IDE instances. Kept separate from
    /// <see cref="ISsisVersionAdapter"/> on purpose: VS compatibility and SSIS runtime
    /// compatibility are different responsibilities.
    /// </summary>
    public interface IVisualStudioLocator
    {
        /// <summary>Enumerates ALL installed VS instances with their capabilities.</summary>
        IReadOnlyList<VisualStudioInstanceInfo> DetectAll();

        /// <summary>
        /// Resolves <paramref name="target"/> to a concrete instance. For <see cref="TargetIde.Auto"/>
        /// it applies the documented selection policy; returns null when no suitable instance exists.
        /// </summary>
        VisualStudioInstanceInfo? Select(TargetIde target);
    }
}
