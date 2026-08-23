using System;
using System.IO;
using System.Linq;
using SsisMcp.Core.Building;
using SsisMcp.IntegrationTests.Support;
using SsisMcp.Ssis;
using SsisMcp.Ssis.Building;
using Xunit;

namespace SsisMcp.IntegrationTests
{
    /// <summary>
    /// Pins the EMPIRICALLY-VERIFIED capability matrix of the Control Flow builder on this runtime,
    /// and pins the known-partial ones so regressions are visible. (Requirement #12.)
    /// </summary>
    public sealed class CapabilitiesTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cap-" + Guid.NewGuid().ToString("N"));
        private readonly PackageService _svc = new PackageService();

        public CapabilitiesTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private string NewTarget()
        {
            var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".dtsx");
            _svc.Save(FixtureBuilder.BuildEmptyWithConnection(), path);
            return path;
        }

        [Fact]
        public void Sequence_commits_and_can_nest_a_configured_task()
        {
            var path = NewTarget();
            var editor = new PackageEditor(_svc);

            var r = editor.Apply(path, b =>
            {
                b.AddTask(TaskKinds.Sequence, "SEQ");
                b.AddTask(TaskKinds.ExecuteSql, "Inner", parentName: "SEQ");
                b.ConfigureExecuteSql("Inner", connection: "Origen", sqlStatement: "SELECT 1;");
            });

            Assert.True(r.Succeeded, r.ErrorCode + ": " + r.Detail);
            var seq = r.Package!.Executables.Single(e => e.Name == "SEQ");
            Assert.Equal("Sequence", seq.TypeName);
            Assert.Contains(seq.Children, c => c.Name == "Inner"); // nested + re-inspected from disk
        }

        [Fact]
        public void ForEachLoop_creates_and_commits_empty()  // enumerator NOT exercised => partial capability
        {
            var path = NewTarget();
            var r = new PackageEditor(_svc).Apply(path, b => b.AddTask(TaskKinds.ForEachLoop, "FEL"));
            Assert.True(r.Succeeded, r.ErrorCode + ": " + r.Detail);
            Assert.Contains(r.Package!.Executables, e => e.Name == "FEL");
        }

        [Fact]
        public void ForLoop_creates_but_requires_configuration_to_validate()
        {
            var path = NewTarget();
            var r = new PackageEditor(_svc).Apply(path, b => b.AddTask(TaskKinds.ForLoop, "FL"));
            // Creation resolves; an unconfigured For Loop (no EvalExpression) fails SSIS validation
            // and is safely rolled back — creation verified, full config still to be exercised.
            Assert.False(r.Succeeded);
            Assert.Equal(nameof(BuilderErrorCode.ValidationFailed), r.ErrorCode);
        }

        // Script Task needs the VSTA design-time, which the Integration Services shared feature
        // installs. Where present it creates + commits; where absent it is reported Unsupported
        // (0x80070057). Portable across both environments — never faked.
        [Fact]
        public void Script_task_creates_when_vsta_present_else_reports_unsupported()
        {
            var path = NewTarget();
            var r = new PackageEditor(_svc).Apply(path, b => b.AddTask(TaskKinds.Script, "Scr"));
            if (r.Succeeded)
                Assert.Contains(r.Package!.Executables, e => e.Name == "Scr");   // VSTA present -> Script Task created + committed
            else
                Assert.Equal(nameof(BuilderErrorCode.Unsupported), r.ErrorCode); // VSTA absent -> honest Unsupported
        }
    }
}
