using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace SsisMcp.IntegrationTests
{
    /// <summary>
    /// REAL out-of-process client: launches the built SsisMcp.Server.exe and speaks JSON-RPC 2.0 over
    /// its stdin/stdout — exactly as Claude Code / Codex would. Proves the handshake, tools discovery,
    /// real write operations, that stdout carries ONLY JSON-RPC (logs go to stderr), and that paths
    /// WITH SPACES work end to end. Skips cleanly if the server exe was not built (e.g. Release-only CI).
    /// </summary>
    public sealed class ExternalMcpClientTests : IDisposable
    {
        private readonly ITestOutputHelper _o;
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcp ext " + Guid.NewGuid().ToString("N")); // NOTE: space in path
        public ExternalMcpClientTests(ITestOutputHelper o) { _o = o; Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static string? FindServerExe()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "SSIS-Agent-MCP.slnx"))) d = d.Parent;
            if (d == null) return null;
            var exe = Path.Combine(d.FullName, "src", "SsisMcp.Server", "bin", "Debug", "net48", "SsisMcp.Server.exe");
            return File.Exists(exe) ? exe : null;
        }

        [Fact]
        public void External_client_handshakes_lists_tools_and_builds_via_stdio()
        {
            var exe = FindServerExe();
            if (exe == null) { _o.WriteLine("server exe not found — skipped"); return; }
            _o.WriteLine("launching: " + exe);

            var stderr = new ConcurrentQueue<string>();
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            using (var p = new Process { StartInfo = psi })
            {
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.Enqueue(e.Data); };
                p.Start();
                p.BeginErrorReadLine();
                var stdin = p.StandardInput; stdin.NewLine = "\n"; stdin.AutoFlush = true;
                var stdout = p.StandardOutput;
                int id = 0;
                var stdoutLines = 0;

                JObject Send(string method, JObject? prms)
                {
                    var req = new JObject { ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = method };
                    if (prms != null) req["params"] = prms;
                    stdin.WriteLine(req.ToString(Newtonsoft.Json.Formatting.None));
                    var line = stdout.ReadLine() ?? throw new Xunit.Sdk.XunitException("no response line for " + method);
                    stdoutLines++;
                    JObject resp;
                    try { resp = JObject.Parse(line); }   // <-- EVERY stdout line MUST be valid JSON-RPC
                    catch (Exception ex) { throw new Xunit.Sdk.XunitException($"stdout not JSON (log leaked?): '{line}' :: {ex.Message}"); }
                    Assert.Equal("2.0", (string?)resp["jsonrpc"]);
                    return resp;
                }

                (JToken result, bool isError, string raw) Call(string tool, JObject args)
                {
                    var resp = Send("tools/call", new JObject { ["name"] = tool, ["arguments"] = args });
                    var res = resp["result"] ?? throw new Xunit.Sdk.XunitException(tool + " error: " + resp);
                    var isErr = (bool)res["isError"]!;
                    var text = (string)res["content"]![0]!["text"]!;
                    JToken parsed = JValue.CreateNull();
                    if (!isErr) { try { parsed = JToken.Parse(text); } catch { parsed = new JValue(text); } }
                    return (parsed, isErr, text);
                }

                // 3) initialize / handshake
                var init = Send("initialize", new JObject { ["protocolVersion"] = "2024-11-05" });
                Assert.Equal("2024-11-05", (string?)init["result"]!["protocolVersion"]);
                Assert.Equal("ssis-agent-mcp", (string?)init["result"]!["serverInfo"]!["name"]);
                // notification: no response, do not read
                stdin.WriteLine(new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }.ToString(Newtonsoft.Json.Formatting.None));

                // 4) real tools list
                var tools = Send("tools/list", null);
                var names = ((JArray)tools["result"]!["tools"]!).Select(t => (string)t["name"]!).ToList();
                _o.WriteLine("tools: " + string.Join(", ", names));
                foreach (var n in new[] { "environment.detect", "package.inspect", "package.create",
                    "controlflow.apply", "dataflow.apply", "package.validate", "package.undo", "connection.test" })
                    Assert.Contains(n, names);

                // 5) real invocations
                var env = Call("environment.detect", new JObject());
                Assert.False(env.isError, env.raw);

                var pkgPath = Path.Combine(_dir, "demo pkg.dtsx");  // space in file name too
                var create = Call("package.create", new JObject { ["packagePath"] = pkgPath, ["name"] = "ExtDemo" });
                Assert.False(create.isError, create.raw);
                Assert.True(File.Exists(pkgPath), "package.create did not write the file");

                var cf = Call("controlflow.apply", new JObject
                {
                    ["packagePath"] = pkgPath,
                    ["operations"] = new JArray
                    {
                        new JObject { ["op"]="addConnection", ["kind"]="oledb-sql", ["name"]="Db", ["dataSource"]=".", ["catalog"]="tempdb" },
                        new JObject { ["op"]="addTask", ["kind"]="DataFlow", ["name"]="DFT" },
                    }
                });
                Assert.False(cf.isError, cf.raw);
                Assert.True((bool)cf.result["succeeded"]!, cf.raw);

                var df = Call("dataflow.apply", new JObject
                {
                    ["packagePath"] = pkgPath, ["dataFlowTask"] = "DFT",
                    ["operations"] = new JArray
                    {
                        new JObject { ["op"]="addComponent", ["kind"]="OleDbSource", ["name"]="Src" },
                        new JObject { ["op"]="configureOleDbSource", ["name"]="Src", ["connection"]="Db", ["accessMode"]=2, ["sqlOrTable"]="SELECT 1 AS n" },
                    }
                });
                Assert.False(df.isError, df.raw);
                Assert.True((bool)df.result["succeeded"]!, df.raw);

                var val = Call("package.validate", new JObject { ["packagePath"] = pkgPath });
                Assert.False(val.isError, val.raw);

                var pkgInspect = Call("package.inspect", new JObject { ["packagePath"] = pkgPath });
                Assert.False(pkgInspect.isError, pkgInspect.raw);
                Assert.Contains("DFT", pkgInspect.raw);

                // close stdin → server exits on EOF
                stdin.Close();
                Assert.True(p.WaitForExit(15000), "server did not exit after stdin EOF");

                // 6) stdout purity: we parsed every stdout line as JSON above (Send throws otherwise).
                Assert.True(stdoutLines >= 6, "expected several JSON-RPC responses");
                _o.WriteLine("stderr lines (logs, correctly off the protocol stream): " + stderr.Count);
                Assert.Contains(stderr, l => l.Contains("ssis-mcp"));   // logs really did go to stderr
            }
        }
    }
}
