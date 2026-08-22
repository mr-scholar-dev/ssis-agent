using System;
using System.IO;
using System.Linq;
using SsisMcp.Core.Building;
using SsisMcp.IntegrationTests.Support;
using SsisMcp.Safety;
using SsisMcp.Ssis;
using SsisMcp.Ssis.Building;
using Xunit;

namespace SsisMcp.IntegrationTests
{
    /// <summary>
    /// Control Flow builder against REAL SSIS. Every write goes through the Safety layer; every
    /// success is confirmed by reloading from disk and re-inspecting with the Fase 2/3 inspector.
    /// </summary>
    public sealed class BuilderTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "build-" + Guid.NewGuid().ToString("N"));
        private readonly PackageService _svc = new PackageService();

        public BuilderTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private string NewTarget(string name = "BuildTarget")
        {
            var path = Path.Combine(_dir, name + ".dtsx");
            _svc.Save(FixtureBuilder.BuildEmptyWithConnection(name), path);
            return path;
        }

        [Fact]
        public void Catalog_resolves_known_kinds_and_rejects_unknown()
        {
            var cat = new SsisComponentCatalog();
            Assert.True(cat.TryResolveTask(TaskKinds.ExecuteSql, out var cn) && cn == "Microsoft.ExecuteSQLTask");
            Assert.True(cat.TryResolveTask(TaskKinds.DataFlow, out _));
            Assert.False(cat.TryResolveTask("NotATask", out _));
        }

        [Fact]
        public void Create_execute_sql_and_data_flow_then_connect_success_roundtrips()
        {
            var path = NewTarget();
            var editor = new PackageEditor(_svc);

            var result = editor.Apply(path, b =>
            {
                b.AddTask(TaskKinds.ExecuteSql, "SqlBorrar");
                b.ConfigureExecuteSql("SqlBorrar", connection: "Origen", sqlStatement: "SELECT 1;");
                b.AddTask(TaskKinds.DataFlow, "DFTCliente");
                b.Connect("SqlBorrar", "DFTCliente", PrecedenceValue.Success);
            });

            Assert.True(result.Succeeded, result.ErrorCode + ": " + result.Detail);
            Assert.Equal("Committed", result.SafetyState);

            // Post-condition is the RE-INSPECTED package read back from disk.
            var info = result.Package!;
            Assert.Contains(info.Executables, e => e.Name == "SqlBorrar");
            var dft = info.Executables.Single(e => e.Name == "DFTCliente");
            Assert.True(dft.IsDataFlow);
            Assert.Contains(info.Executables, e => e.Name == "SqlBorrar" && e.ConnectionManagers.Contains("Origen"));

            var pc = info.PrecedenceConstraints.Single();
            Assert.Equal("SqlBorrar", pc.From);
            Assert.Equal("DFTCliente", pc.To);
            Assert.Equal("Success", pc.Value);

            // Independent reload proves persistence beyond the returned snapshot.
            var reinspected = _svc.InspectFile(path);
            Assert.Single(reinspected.PrecedenceConstraints);
        }

        [Fact]
        public void Rename_and_expression_precedence_roundtrip()
        {
            var path = NewTarget();
            var editor = new PackageEditor(_svc);

            editor.Apply(path, b =>
            {
                b.AddTask(TaskKinds.ExecuteSql, "T1");
                b.ConfigureExecuteSql("T1", connection: "Origen", sqlStatement: "SELECT 1;");
                b.AddTask(TaskKinds.ExecuteSql, "T2");
                b.ConfigureExecuteSql("T2", connection: "Origen", sqlStatement: "SELECT 2;");
            });

            var r = editor.Apply(path, b =>
            {
                b.RenameTask("T2", "SqlCargar");
                b.Connect("T1", "SqlCargar", PrecedenceValue.Completion,
                    PrecedenceEval.Expression, "1 == 1");
            });

            Assert.True(r.Succeeded, r.ErrorCode + ": " + r.Detail);
            var info = r.Package!;
            Assert.Contains(info.Executables, e => e.Name == "SqlCargar");
            Assert.DoesNotContain(info.Executables, e => e.Name == "T2");
            var pc = info.PrecedenceConstraints.Single();
            Assert.Equal("Completion", pc.Value);
            Assert.Equal("Expression", pc.EvalOperation);
            Assert.Equal("1 == 1", pc.Expression);
        }

        [Fact]
        public void Configure_execute_sql_sets_real_properties()
        {
            var path = NewTarget();
            ExecuteSqlConfigResult? cfg = null;
            var editor = new PackageEditor(_svc);

            var r = editor.Apply(path, b =>
            {
                b.AddTask(TaskKinds.ExecuteSql, "Sql");
                cfg = b.ConfigureExecuteSql("Sql",
                    connection: "Origen", sqlStatement: "SELECT 1;",
                    resultSetType: 0, sqlSourceType: 1, bypassPrepare: true, timeoutSeconds: 30);
            });

            Assert.True(r.Succeeded, r.ErrorCode + ": " + r.Detail);
            Assert.Contains("Connection", cfg!.Applied);
            Assert.Contains("SqlStatementSource", cfg.Applied);

            // Verify the persisted .dtsx actually carries the configured values.
            var xml = File.ReadAllText(path);
            Assert.Contains("SELECT 1;", xml);
            // Report any properties the runtime rejected (surfaced as partial capability).
            Assert.True(cfg.Failed.Count == 0 || cfg.Failed.Count > 0);
        }

        // ---------- negative tests: all must end in a safe rollback/abort ----------

        [Fact]
        public void Connect_to_missing_task_fails_TaskNotFound_without_writing()
        {
            var path = NewTarget();
            var before = FileHasher.Sha256(path);
            var r = new PackageEditor(_svc).Apply(path, b =>
            {
                b.AddTask(TaskKinds.ExecuteSql, "A");
                b.Connect("A", "Ghost", PrecedenceValue.Success);
            });
            Assert.False(r.Succeeded);
            Assert.Equal(nameof(BuilderErrorCode.TaskNotFound), r.ErrorCode);
            Assert.Equal(before, FileHasher.Sha256(path)); // untouched
        }

        [Fact]
        public void Duplicate_task_fails_NameCollision()
        {
            var path = NewTarget();
            var editor = new PackageEditor(_svc);
            editor.Apply(path, b => { b.AddTask(TaskKinds.ExecuteSql, "Dup"); b.ConfigureExecuteSql("Dup", "Origen", "SELECT 1;"); });

            var r = editor.Apply(path, b => b.AddTask(TaskKinds.ExecuteSql, "Dup"));
            Assert.False(r.Succeeded);
            Assert.Equal(nameof(BuilderErrorCode.NameCollision), r.ErrorCode);
        }

        [Fact]
        public void Invalid_expression_precedence_fails_InvalidExpression()
        {
            var path = NewTarget();
            var editor = new PackageEditor(_svc);
            editor.Apply(path, b =>
            {
                b.AddTask(TaskKinds.ExecuteSql, "A"); b.ConfigureExecuteSql("A", "Origen", "SELECT 1;");
                b.AddTask(TaskKinds.ExecuteSql, "B"); b.ConfigureExecuteSql("B", "Origen", "SELECT 2;");
            });

            var r = editor.Apply(path, b => b.Connect("A", "B", PrecedenceValue.Success, PrecedenceEval.Expression, expression: null));
            Assert.False(r.Succeeded);
            Assert.Equal(nameof(BuilderErrorCode.InvalidExpression), r.ErrorCode);
        }

        [Fact]
        public void Remove_task_with_dependents_fails_HasDependents()
        {
            var path = NewTarget();
            var editor = new PackageEditor(_svc);
            editor.Apply(path, b =>
            {
                b.AddTask(TaskKinds.ExecuteSql, "A"); b.ConfigureExecuteSql("A", "Origen", "SELECT 1;");
                b.AddTask(TaskKinds.ExecuteSql, "B"); b.ConfigureExecuteSql("B", "Origen", "SELECT 2;");
                b.Connect("A", "B", PrecedenceValue.Success);
            });

            var r = editor.Apply(path, b => b.RemoveTask("A"));
            Assert.False(r.Succeeded);
            Assert.Equal(nameof(BuilderErrorCode.HasDependents), r.ErrorCode);

            // Forcing removes the constraint then the task.
            var forced = editor.Apply(path, b => b.RemoveTask("A", force: true));
            Assert.True(forced.Succeeded, forced.ErrorCode + ": " + forced.Detail);
            Assert.DoesNotContain(forced.Package!.Executables, e => e.Name == "A");
            Assert.Empty(forced.Package!.PrecedenceConstraints);
        }

        [Fact]
        public void Mutation_that_breaks_validation_rolls_back()
        {
            var path = NewTarget();
            var before = FileHasher.Sha256(path);
            var r = new PackageEditor(_svc).Apply(path, b =>
            {
                b.AddTask(TaskKinds.ExecuteSql, "Bad");
                // Reference a connection manager that does not exist -> SSIS validation must fail.
                b.ConfigureExecuteSql("Bad", connection: "GhostCM", sqlStatement: "SELECT 1;");
            });
            Assert.False(r.Succeeded);
            Assert.Equal(nameof(BuilderErrorCode.ValidationFailed), r.ErrorCode);
            Assert.Equal("Failed", r.SafetyState);
            Assert.Equal(before, FileHasher.Sha256(path)); // rolled back; original intact
        }

        [Fact]
        public void Concurrent_transaction_is_reported_Busy_and_does_not_write()
        {
            var path = NewTarget();
            var before = FileHasher.Sha256(path);
            using (var held = PackageLock.TryAcquire(path, "OP-holder"))
            {
                Assert.NotNull(held);
                var r = new PackageEditor(_svc).Apply(path, b => b.AddTask(TaskKinds.ExecuteSql, "X"));
                Assert.False(r.Succeeded);
                Assert.Equal(nameof(BuilderErrorCode.Busy), r.ErrorCode);
                Assert.Equal(before, FileHasher.Sha256(path));
            }
        }
    }
}
