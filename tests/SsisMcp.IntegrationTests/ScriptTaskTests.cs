using System;
using System.IO;
using SsisMcp.Core.Building;
using SsisMcp.Core.Execution;
using SsisMcp.Ssis;
using SsisMcp.Ssis.Building;
using SsisMcp.Ssis.Execution;
using Xunit;
using Xunit.Abstractions;
using Dts = Microsoft.SqlServer.Dts.Runtime;

namespace SsisMcp.IntegrationTests
{
    /// <summary>
    /// Reusable Script Task capability: configure C# source + ReadOnly variables, PRECOMPILE via the
    /// VSTA design-time, then execute headless with the licensed dtexec and confirm the script ran
    /// (it writes a file whose path comes from a package variable). Portable:
    /// - VSTA design-time absent  → ConfigureScriptTask reports UnsupportedEnvironment (test skips);
    /// - no licensed signed host   → execution EnvironmentBlocked (test skips the run assertion).
    /// A second test pins the structured compile-error path.
    /// </summary>
    public sealed class ScriptTaskTests : IDisposable
    {
        private readonly ITestOutputHelper _o;
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "scr-" + Guid.NewGuid().ToString("N"));
        private readonly PackageService _svc = new PackageService();
        public ScriptTaskTests(ITestOutputHelper o) { _o = o; Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private string NewPackageWithVar(string varName, string varValue, out string path)
        {
            var pkg = new Dts.Package { Name = "S" };
            pkg.Variables.Add(varName, false, "User", varValue);
            path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".dtsx");
            _svc.Save(pkg, path);
            Assert.True(new PackageEditor(_svc).Apply(path, b => b.AddTask(TaskKinds.Script, "Scr")).Succeeded);
            return path;
        }

        [Fact]
        public void Script_task_precompiles_configures_variables_and_executes()
        {
            var outFile = Path.Combine(_dir, "scr-out.txt");
            NewPackageWithVar("OutPath", outFile, out var path);

            var body =
                "            string p = Dts.Variables[\"User::OutPath\"].Value.ToString();\n" +
                "            System.IO.File.WriteAllText(p, \"script-task-ran\");\n";
            var source = ScriptTaskSource.CSharpMain(body);

            var r = new PackageEditor(_svc).Apply(path, b =>
                b.ConfigureScriptTask("Scr", source, readOnlyVariables: new[] { "User::OutPath" }));

            if (!r.Succeeded && r.ErrorCode == nameof(BuilderErrorCode.UnsupportedEnvironment))
            { _o.WriteLine("VSTA absent -> skipped: " + r.Detail); return; }   // portable skip
            Assert.True(r.Succeeded, r.ErrorCode + ": " + r.Detail);           // configured + PRECOMPILED + validated

            var exec = new SsdtDebugExecutionHost().Execute(path);
            _o.WriteLine("outcome=" + exec.Outcome);
            if (exec.Outcome == ExecutionOutcome.EnvironmentBlocked) return;   // portable skip (no licensed host)
            Assert.Equal(ExecutionOutcome.Success, exec.Outcome);
            Assert.True(File.Exists(outFile), "script task did not write its file");
            Assert.Equal("script-task-ran", File.ReadAllText(outFile));
        }

        [Fact]
        public void Invalid_csharp_reports_structured_compile_error()
        {
            NewPackageWithVar("OutPath", "x", out var path);
            var badSource = ScriptTaskSource.CSharpMain("            this will not compile @@@;\n");

            var r = new PackageEditor(_svc).Apply(path, b => b.ConfigureScriptTask("Scr", badSource));

            Assert.False(r.Succeeded);
            // VSTA present -> ScriptCompileFailed (with messages); VSTA absent -> UnsupportedEnvironment.
            Assert.True(r.ErrorCode == nameof(BuilderErrorCode.ScriptCompileFailed)
                     || r.ErrorCode == nameof(BuilderErrorCode.UnsupportedEnvironment),
                "unexpected code: " + r.ErrorCode + " / " + r.Detail);
        }
    }
}
