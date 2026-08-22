using System;
using System.IO;
using System.Linq;
using SsisMcp.Core.Environment;
using Xunit;

namespace SsisMcp.UnitTests
{
    public class GacScannerTests
    {
        [Fact]
        public void Parses_versions_from_gac_style_folder_names()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "gactest_" + Guid.NewGuid().ToString("N"));
            var gacRoot = Path.Combine(tmp, "GAC_MSIL");
            var asmDir = Path.Combine(gacRoot, "Microsoft.SqlServer.ManagedDTS");
            Directory.CreateDirectory(Path.Combine(asmDir, "v4.0_17.0.0.0__89845dcd8080cc91"));
            Directory.CreateDirectory(Path.Combine(asmDir, "v4.0_13.0.0.0__89845dcd8080cc91"));
            try
            {
                var versions = GacScanner.FindVersions("Microsoft.SqlServer.ManagedDTS", new[] { gacRoot });
                Assert.Equal(2, versions.Count);
                Assert.Equal(new Version(17, 0, 0, 0), versions[0]); // sorted descending
                Assert.Equal(new Version(13, 0, 0, 0), versions[1]);
            }
            finally
            {
                Directory.Delete(tmp, recursive: true);
            }
        }

        [Fact]
        public void Returns_empty_when_assembly_absent()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "gactest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var versions = GacScanner.FindVersions("Nonexistent.Assembly", new[] { tmp });
                Assert.Empty(versions);
            }
            finally
            {
                Directory.Delete(tmp, recursive: true);
            }
        }
    }
}
