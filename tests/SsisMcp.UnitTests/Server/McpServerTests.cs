using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SsisMcp.Server;
using Xunit;

namespace SsisMcp.UnitTests.Server
{
    public sealed class McpServerTests
    {
        private static JObject Req(int id, string method, JObject? prms = null)
        {
            var o = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (prms != null) o["params"] = prms;
            return o;
        }

        [Fact]
        public void Initialize_reports_tools_capability_and_server_info()
        {
            var resp = new McpServer().Dispatch(Req(1, "initialize"))!;
            Assert.Equal("ssis-agent-mcp", (string?)resp["result"]!["serverInfo"]!["name"]);
            Assert.NotNull(resp["result"]!["capabilities"]!["tools"]);
        }

        [Fact]
        public void ToolsList_exposes_exactly_the_five_readonly_tools()
        {
            var resp = new McpServer().Dispatch(Req(2, "tools/list"))!;
            var names = ((JArray)resp["result"]!["tools"]!).Select(t => (string)t!["name"]!).ToArray();

            Assert.Equal(
                new[] { "environment.detect", "project.inspect", "package.inspect", "controlflow.inspect", "dataflow.inspect" }.OrderBy(x => x),
                names.OrderBy(x => x));

            // No write/mutation tools may be exposed in this phase.
            Assert.DoesNotContain(names, n => n!.StartsWith("changes.") || n.StartsWith("package.backup")
                || n.Contains("apply") || n.Contains("undo"));
        }

        [Fact]
        public void Notifications_initialized_yields_no_response()
        {
            var resp = new McpServer().Dispatch(new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
            Assert.Null(resp);
        }

        [Fact]
        public void Unknown_method_returns_jsonrpc_error()
        {
            var resp = new McpServer().Dispatch(Req(3, "does/notExist"))!;
            Assert.Equal(-32601, (int)resp["error"]!["code"]!);
        }

        [Fact]
        public void Unknown_tool_returns_error()
        {
            var resp = new McpServer().Dispatch(Req(4, "tools/call", new JObject { ["name"] = "nope", ["arguments"] = new JObject() }))!;
            Assert.NotNull(resp["error"]);
        }

        [Fact]
        public void EnvironmentDetect_tool_returns_parseable_json_content()
        {
            var resp = new McpServer().Dispatch(Req(5, "tools/call",
                new JObject { ["name"] = "environment.detect", ["arguments"] = new JObject() }))!;

            Assert.False((bool)resp["result"]!["isError"]!);
            var text = (string?)resp["result"]!["content"]![0]!["text"];
            var parsed = JObject.Parse(text!); // structured DTO, not free text
            Assert.NotNull(parsed["checks"]);
            Assert.NotNull(parsed["coreUsable"]);
        }

        [Fact]
        public void Run_processes_newline_delimited_stdio()
        {
            var input = new StringReader(
                Req(1, "initialize").ToString(Newtonsoft.Json.Formatting.None) + "\n" +
                Req(2, "tools/list").ToString(Newtonsoft.Json.Formatting.None) + "\n");
            var output = new StringWriter();

            new McpServer().Run(input, output);

            var lines = output.ToString().Split('\n').Where(l => l.Length > 0).ToArray();
            Assert.Equal(2, lines.Length);
            Assert.All(lines, l => Assert.NotNull(JObject.Parse(l)["result"])); // both are valid JSON-RPC results
        }
    }
}
