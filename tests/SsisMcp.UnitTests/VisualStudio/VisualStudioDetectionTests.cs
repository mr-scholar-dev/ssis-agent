using System.Collections.Generic;
using System.Linq;
using SsisMcp.Core.Ide;
using SsisMcp.VisualStudioBridge;
using Xunit;

namespace SsisMcp.UnitTests.VisualStudio
{
    /// <summary>Deterministic detection/selection tests over synthetic vswhere records + fake probes.</summary>
    public class VisualStudioDetectionTests
    {
        private static VsWhereRecord Vs2022(string edition = "Community") =>
            new VsWhereRecord { InstallationPath = @"C:\VS2022", DisplayVersion = "17.14.35", ProductId = "Microsoft.VisualStudio.Product." + edition };
        private static VsWhereRecord Vs2026(string edition = "Community") =>
            new VsWhereRecord { InstallationPath = @"C:\VS2026", DisplayVersion = "18.7.3", ProductId = "Microsoft.VisualStudio.Product." + edition };

        private static WindowsVisualStudioLocator Locator(IReadOnlyList<VsWhereRecord> recs,
            System.Func<string, bool> hasIde, System.Func<string, bool> hasDesigner) =>
            new WindowsVisualStudioLocator(() => recs, hasIde, hasDesigner);

        [Fact]
        public void Maps_versions_to_generations()
        {
            Assert.Equal(TargetIde.VS2022, VsInstanceClassifier.Classify(Vs2022(), true, true).Generation);
            Assert.Equal(TargetIde.VS2026, VsInstanceClassifier.Classify(Vs2026(), true, true).Generation);
        }

        [Fact]
        public void BuildTools_has_no_ide_so_cannot_open_dtproj()
        {
            var info = VsInstanceClassifier.Classify(Vs2022("BuildTools"), hasIde: false, hasSsisDesigner: false);
            Assert.False(info.CanOpenDtproj);
            Assert.Contains("no-ide", info.Capabilities);
            Assert.Contains("DesignerUnavailable", info.Capabilities);
        }

        [Fact]
        public void Ide_without_ssis_extension_is_DesignerUnavailable()
        {
            var info = VsInstanceClassifier.Classify(Vs2026(), hasIde: true, hasSsisDesigner: false);
            Assert.False(info.CanOpenDtproj);
            Assert.False(info.SsisDesignerAvailable);
            Assert.True(info.BridgeCompatible); // the MCP bridge still targets the IDE
            Assert.Contains("DesignerUnavailable", info.Capabilities);
        }

        [Fact]
        public void Vs2022_with_ssis_extension_is_designer_layout_testing_ready()
        {
            var info = VsInstanceClassifier.Classify(Vs2022(), hasIde: true, hasSsisDesigner: true, extensionVersion: "1.5");
            Assert.True(info.CanOpenDtproj);
            Assert.True(info.VisualStudioInstalled);
            Assert.True(info.VisualStudioIdeAvailable);
            Assert.True(info.SsisProjectsExtensionInstalled);
            Assert.True(info.SsisDesignerAvailable);
            Assert.Equal("1.5", info.SsisProjectsExtensionVersion);
            Assert.Equal("DesignerVerificationTarget", info.Role);
            Assert.Equal("Ready", info.DesignerLayoutTesting); // <-- flips to READY once VS2022+SSDT is installed
        }

        [Fact]
        public void Vs2026_designer_stays_blocked_even_if_extension_present()
        {
            // Policy: VS2026 is a bridge/architectural target; its Designer is EnvironmentBlocked
            // until Microsoft ships the SSIS Projects extension for it — never faked.
            var info = VsInstanceClassifier.Classify(Vs2026(), hasIde: true, hasSsisDesigner: true);
            Assert.Equal("BridgeTarget", info.Role);
            Assert.Equal("EnvironmentBlocked", info.DesignerLayoutTesting);
            Assert.True(info.BridgeCompatible); // the MCP bridge still targets VS2026
        }

        [Fact]
        public void BuildTools_reports_all_four_facts_distinctly()
        {
            var info = VsInstanceClassifier.Classify(Vs2022("BuildTools"), hasIde: false, hasSsisDesigner: false);
            Assert.True(info.VisualStudioInstalled);
            Assert.False(info.VisualStudioIdeAvailable);
            Assert.False(info.SsisProjectsExtensionInstalled);
            Assert.False(info.SsisDesignerAvailable);
        }

        [Fact]
        public void Select_auto_prefers_designer_capable_newest()
        {
            // 2022 has the designer, 2026 does not → auto must pick 2022 (usable) over newer-but-unusable 2026.
            var loc = Locator(new[] { Vs2022(), Vs2026() },
                hasIde: _ => true,
                hasDesigner: p => p.Contains("VS2022"));
            Assert.Equal(TargetIde.VS2022, loc.Select(TargetIde.Auto)!.Generation);
        }

        [Fact]
        public void Select_explicit_returns_null_when_absent()
        {
            var loc = Locator(new[] { Vs2026() }, _ => true, _ => false);
            Assert.Null(loc.Select(TargetIde.VS2022));      // no 2022 present
            Assert.NotNull(loc.Select(TargetIde.VS2026));
        }

        [Fact]
        public void Neither_installed_yields_empty()
        {
            var loc = Locator(new VsWhereRecord[0], _ => true, _ => true);
            Assert.Empty(loc.DetectAll());
            Assert.Null(loc.Select(TargetIde.Auto));
        }

        [Fact]
        public void Real_host_detection_reports_no_designer_capable_instance()
        {
            // Honest environment check on THIS host: instances exist but none can open .dtproj
            // (VS2022 is Build Tools; VS2026 lacks the SSIS Projects extension).
            var instances = new WindowsVisualStudioLocator().DetectAll();
            Assert.NotEmpty(instances);
            Assert.DoesNotContain(instances, i => i.CanOpenDtproj);
        }
    }
}
