using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SsisMcp.IntegrationTests.Support;
using SsisMcp.Server;
using SsisMcp.Ssis;
using SsisMcp.Ssis.Inspection;
using Xunit;

namespace SsisMcp.IntegrationTests
{
    /// <summary>End-to-end: the read-only MCP server and project inspection over REAL SSIS artifacts.</summary>
    public sealed class ServerAndProjectTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "srv-" + Guid.NewGuid().ToString("N"));
        private readonly PackageService _svc = new PackageService();

        public ServerAndProjectTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void Server_package_inspect_over_stdio_returns_control_flow()
        {
            var pkgPath = Path.Combine(_dir, "Package.dtsx");
            _svc.Save(FixtureBuilder.BuildControlFlowWithPrecedence(), pkgPath);

            var call = new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "package.inspect",
                    ["arguments"] = new JObject { ["packagePath"] = pkgPath }
                }
            };
            var output = new StringWriter();
            new McpServer().Run(new StringReader(call.ToString(Newtonsoft.Json.Formatting.None) + "\n"), output);

            var resp = JObject.Parse(output.ToString().Trim());
            Assert.False((bool)resp["result"]!["isError"]!);
            var dto = JObject.Parse((string)resp["result"]!["content"]![0]!["text"]!);

            var execNames = ((JArray)dto["executables"]!).Select(e => (string?)e!["name"]).ToArray();
            Assert.Contains("SqlBorrar", execNames);
            Assert.Contains("SqlCargar", execNames);
            Assert.Single((JArray)dto["precedenceConstraints"]!);
            Assert.NotNull(dto["packageFormatVersion"]);
        }

        [Fact]
        public void Project_inspect_reports_target_version_and_conservative_diagnostics()
        {
            // Build a real .dtsx and a representative .dtproj referencing it.
            var pkgPath = Path.Combine(_dir, "Package.dtsx");
            _svc.Save(FixtureBuilder.BuildControlFlowWithPrecedence(), pkgPath);

            var dtproj = Path.Combine(_dir, "Sample.dtproj");
            File.WriteAllText(dtproj, SampleDtproj());

            var info = new ProjectInspector().Inspect(dtproj);

            Assert.Equal("SQLServer2016", info.TargetServerVersion);
            Assert.Equal("Project", info.DeploymentModel);
            Assert.Contains(info.Packages, p => p.Name == "Package.dtsx" && p.FileExists);

            var d = info.Diagnostics;
            Assert.NotNull(d.DetectedSsisRuntime);
            Assert.Contains("Visual Studio 2022", d.CompatibleVisualStudio);
            Assert.Contains("Visual Studio 2026", d.CompatibleVisualStudio);
            Assert.Equal("SQLServer2016", d.TargetServerVersion);

            // 2016 must NOT be reported as verified (no real 2016 build proven on this runtime).
            Assert.False(d.TargetServerVersionVerified);
            Assert.Contains(d.KnownIncompatibilities, m => m.Contains("2016"));
        }

        private static string SampleDtproj() =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
            "  <DeploymentModel>Project</DeploymentModel>\n" +
            "  <DeploymentModelSpecificContent>\n" +
            "    <Manifest>\n" +
            "      <SSIS:Project xmlns:SSIS=\"www.microsoft.com/SqlServer/SSIS\">\n" +
            "        <SSIS:Properties>\n" +
            "          <SSIS:Property SSIS:Name=\"TargetServerVersion\">SQLServer2016</SSIS:Property>\n" +
            "        </SSIS:Properties>\n" +
            "        <SSIS:Packages>\n" +
            "          <SSIS:Package SSIS:Name=\"Package.dtsx\" />\n" +
            "        </SSIS:Packages>\n" +
            "      </SSIS:Project>\n" +
            "    </Manifest>\n" +
            "  </DeploymentModelSpecificContent>\n" +
            "  <TargetServerVersion>SQLServer2016</TargetServerVersion>\n" +
            "</Project>\n";
    }
}
