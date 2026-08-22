using System;
using System.IO;
using System.Linq;
using SsisMcp.Core.Safety;
using SsisMcp.Safety;
using SsisMcp.Ssis;
using Xunit;
using Dts = Microsoft.SqlServer.Dts.Runtime;

namespace SsisMcp.IntegrationTests
{
    /// <summary>
    /// End-to-end safety: a real .dtsx is edited through the transactional pipeline, validated by
    /// real SSIS, then committed. Proves the SSIS-agnostic Safety layer works with the SSIS validator.
    /// </summary>
    public sealed class SafetyWithRealSsisTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "safety-it-" + Guid.NewGuid().ToString("N"));

        public SafetyWithRealSsisTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void Apply_commits_a_real_task_addition_validated_by_ssis()
        {
            var svc = new PackageService();
            var path = Path.Combine(_dir, "Package.dtsx");
            svc.Save(svc.CreateMinimalPackage("SafePkg", "2016"), path);

            var editor = new TransactionalPackageEditor(new SsisPackageValidator(svc), new InMemoryAuditTrail());

            var result = editor.Apply(path, tempPath =>
            {
                var pkg = svc.Load(tempPath);
                var host = (Dts.TaskHost)pkg.Executables.Add("Microsoft.ExecuteSQLTask");
                host.Name = "SqlExtra";
                host.Properties["Connection"].SetValue(host, "PracticaOrigen");
                host.Properties["SqlStatementSource"].SetValue(host, "SELECT 2;");
                svc.Save(pkg, tempPath);
            });

            Assert.Equal(TransactionState.Committed, result.State);
            Assert.True(result.Validation!.IsValid);
            Assert.NotNull(result.BackupPath);

            // The committed original now contains the new task and still validates.
            var reloaded = svc.Load(path);
            var info = svc.Inspect(reloaded);
            Assert.Contains(info.Executables, e => e.Name == "SqlExtra");
            Assert.Equal(Dts.DTSExecResult.Success, svc.Validate(reloaded));
        }

        [Fact]
        public void Preview_does_not_modify_the_real_package()
        {
            var svc = new PackageService();
            var path = Path.Combine(_dir, "Preview.dtsx");
            svc.Save(svc.CreateMinimalPackage("PrevPkg", "2016"), path);
            var before = FileHasher.Sha256(path);

            var editor = new TransactionalPackageEditor(new SsisPackageValidator(svc), new InMemoryAuditTrail());
            var result = editor.Preview(path, tempPath =>
            {
                var pkg = svc.Load(tempPath);
                var host = (Dts.TaskHost)pkg.Executables.Add("Microsoft.ExecuteSQLTask");
                host.Name = "WouldBeAdded";
                host.Properties["Connection"].SetValue(host, "PracticaOrigen");
                host.Properties["SqlStatementSource"].SetValue(host, "SELECT 3;");
                svc.Save(pkg, tempPath);
            });

            Assert.Equal(TransactionState.PreviewOnly, result.State);
            Assert.Equal(before, FileHasher.Sha256(path)); // real file untouched
        }
    }
}
