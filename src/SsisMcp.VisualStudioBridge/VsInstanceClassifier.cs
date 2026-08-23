using System;
using SsisMcp.Core.Ide;

namespace SsisMcp.VisualStudioBridge
{
    /// <summary>Raw facts about a Visual Studio install as reported by vswhere.</summary>
    public sealed class VsWhereRecord
    {
        public string InstallationPath { get; set; } = "";
        public string DisplayVersion { get; set; } = "";     // e.g. "17.14.35" or "18.7.3"
        public string ProductId { get; set; } = "";          // e.g. Microsoft.VisualStudio.Product.Community / .BuildTools
    }

    /// <summary>
    /// Pure classification of a vswhere record into a <see cref="VisualStudioInstanceInfo"/>.
    /// Filesystem facts (IDE present, SSIS designer extension present) are injected so this is
    /// deterministically unit-testable with synthetic inputs.
    /// </summary>
    public static class VsInstanceClassifier
    {
        public static VisualStudioInstanceInfo Classify(VsWhereRecord rec, bool hasIde, bool hasSsisDesigner, string? extensionVersion = null)
        {
            var major = MajorOf(rec.DisplayVersion);
            var generation = major == 17 ? TargetIde.VS2022 : major == 18 ? TargetIde.VS2026 : TargetIde.Auto;

            var info = new VisualStudioInstanceInfo
            {
                Version = rec.DisplayVersion,
                Generation = generation,
                Edition = EditionOf(rec.ProductId),
                InstallationPath = rec.InstallationPath,
                SsisProjectsExtensionInstalled = hasSsisDesigner,
                SsisProjectsExtensionVersion = hasSsisDesigner ? extensionVersion : null,
                // Opening a .dtproj in the designer requires BOTH a full IDE and the SSIS designer.
                CanOpenDtproj = hasIde && hasSsisDesigner,
                BridgeCompatible = hasIde && hasSsisDesigner
            };

            info.Capabilities.Add(hasIde ? "ide" : "no-ide");
            if (hasSsisDesigner) info.Capabilities.Add("ssis-designer");
            else info.Capabilities.Add("DesignerUnavailable");
            return info;
        }

        private static int MajorOf(string version)
        {
            var dot = version.IndexOf('.');
            var head = dot > 0 ? version.Substring(0, dot) : version;
            return int.TryParse(head, out var m) ? m : 0;
        }

        private static string EditionOf(string productId)
        {
            if (string.IsNullOrEmpty(productId)) return "Unknown";
            var i = productId.LastIndexOf('.');
            return i >= 0 && i < productId.Length - 1 ? productId.Substring(i + 1) : productId;
        }
    }
}
