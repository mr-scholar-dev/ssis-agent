using System;
using System.IO;
using SsisMcp.Core.Building;
using SsisMcp.Safety;
using SsisMcp.Ssis;
using SsisMcp.Ssis.Building;
using Xunit;
using Dts = Microsoft.SqlServer.Dts.Runtime;

namespace SsisMcp.IntegrationTests
{
    /// <summary>Negative Data Flow builder paths — all must end in a structured error + safe rollback/abort.</summary>
    public sealed class DataFlowNegativeTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "dfneg-" + Guid.NewGuid().ToString("N"));
        private readonly PackageService _svc = new PackageService();

        public DataFlowNegativeTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private string Target()
        {
            var pkg = new Dts.Package { Name = "DF" };
            var cm = pkg.Connections.Add("OLEDB"); cm.Name = "Db";
            cm.ConnectionString = "Data Source=.;Initial Catalog=tempdb;Provider=MSOLEDBSQL;Integrated Security=SSPI;";
            var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".dtsx");
            _svc.Save(pkg, path);
            Assert.True(new PackageEditor(_svc).Apply(path, b => b.AddTask(TaskKinds.DataFlow, "DFT")).Succeeded);
            return path;
        }

        [Fact]
        public void Unknown_component_kind_is_Unsupported()
        {
            var r = new PackageEditor(_svc).ApplyDataFlow(Target(), "DFT", b => b.AddComponent("NotAComponent", "X"));
            Assert.False(r.Succeeded);
            Assert.Equal(nameof(BuilderErrorCode.Unsupported), r.ErrorCode);
        }

        [Fact]
        public void Connect_to_missing_component_is_TaskNotFound()
        {
            var r = new PackageEditor(_svc).ApplyDataFlow(Target(), "DFT", b =>
            {
                b.AddComponent(ComponentKinds.DataConversion, "A");
                b.Connect("A", "Ghost");
            });
            Assert.False(r.Succeeded);
            Assert.Equal(nameof(BuilderErrorCode.TaskNotFound), r.ErrorCode);
        }

        [Fact]
        public void Bad_connection_manager_fails_without_commit()
        {
            var path = Target();
            var before = FileHasher.Sha256(path);
            var r = new PackageEditor(_svc).ApplyDataFlow(path, "DFT", b =>
            {
                b.AddComponent(ComponentKinds.OleDbSource, "Src");
                b.ConfigureOleDbSource("Src", "GhostCM", 2, "SELECT 1 AS X");
            });
            Assert.False(r.Succeeded);
            Assert.Equal(before, FileHasher.Sha256(path)); // untouched
        }

        [Fact]
        public void Remove_component_with_attached_path_is_HasDependents()
        {
            var path = Target();
            var editor = new PackageEditor(_svc);
            var r = editor.ApplyDataFlow(path, "DFT", b =>
            {
                b.AddComponent(ComponentKinds.DataConversion, "A");
                b.AddComponent(ComponentKinds.DataConversion, "B");
                b.Connect("A", "B");
                b.RemoveComponent("A"); // A has an attached path
            });
            Assert.False(r.Succeeded);
            Assert.Equal(nameof(BuilderErrorCode.HasDependents), r.ErrorCode);
        }

        [Fact]
        public void Concurrent_lock_reports_Busy_and_does_not_write()
        {
            var path = Target();
            var before = FileHasher.Sha256(path);
            using (var held = PackageLock.TryAcquire(path, "OP-holder"))
            {
                Assert.NotNull(held);
                var r = new PackageEditor(_svc).ApplyDataFlow(path, "DFT", b => b.AddComponent(ComponentKinds.DataConversion, "A"));
                Assert.False(r.Succeeded);
                Assert.Equal(nameof(BuilderErrorCode.Busy), r.ErrorCode);
                Assert.Equal(before, FileHasher.Sha256(path));
            }
        }
    }
}
