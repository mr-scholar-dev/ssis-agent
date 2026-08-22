using System;
using System.IO;
using System.Linq;
using SsisMcp.Ssis;
using Xunit;
using Dts = Microsoft.SqlServer.Dts.Runtime;

namespace SsisMcp.IntegrationTests
{
    /// <summary>
    /// Phase 1 acceptance: exercises the real SSIS Object Model (no mocks). Proves create → save →
    /// reload → inspect → validate preserves structure and yields a valid, non-corrupt package.
    /// </summary>
    public class PackageRoundtripTests : IDisposable
    {
        private readonly string _dir =
            Path.Combine(Path.GetTempPath(), "ssis-it-" + Guid.NewGuid().ToString("N"));

        public PackageRoundtripTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

        [Fact]
        public void Create_save_reload_preserves_structure_and_validates()
        {
            var svc = new PackageService();
            var path = Path.Combine(_dir, "roundtrip.dtsx");

            var pkg = svc.CreateMinimalPackage("PoCPackage", "2016");
            svc.Save(pkg, path);
            Assert.True(new FileInfo(path).Length > 0);

            var reloaded = svc.Load(path);
            var info = svc.Inspect(reloaded);

            Assert.Equal("PoCPackage", info.Name);
            Assert.Contains(info.Executables, e => e.Name == "SEQ_Main");
            // SqlBorrar is nested inside SEQ_Main (hierarchical Control Flow).
            var seq = info.Executables.Single(e => e.Name == "SEQ_Main");
            Assert.Contains(seq.Children, e => e.Name == "SqlBorrar");
            Assert.Contains(info.Connections, c => c.Name == "PracticaOrigen");

            Assert.Equal(Dts.DTSExecResult.Success, svc.Validate(reloaded));
        }

        [Fact]
        public void Load_missing_file_throws()
        {
            var svc = new PackageService();
            Assert.Throws<FileNotFoundException>(() => svc.Load(Path.Combine(_dir, "nope.dtsx")));
        }
    }
}
