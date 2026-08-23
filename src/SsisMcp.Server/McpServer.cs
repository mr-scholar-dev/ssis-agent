using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using SsisMcp.Core.Environment;
using SsisMcp.Designer;
using SsisMcp.Ssis;
using SsisMcp.Ssis.Building;
using SsisMcp.Ssis.Execution;
using SsisMcp.Ssis.Inspection;

namespace SsisMcp.Server
{
    /// <summary>
    /// Minimal MCP server (JSON-RPC 2.0 over newline-delimited stdio). READ-ONLY only: it exposes
    /// inspection tools so an agent can precisely understand a project before any modification.
    /// Write tools are intentionally NOT registered yet. Dispatch is separated from the stdio loop
    /// so it can be unit-tested deterministically.
    /// </summary>
    public sealed class McpServer
    {
        private const string ProtocolVersion = "2024-11-05";

        private static readonly JsonSerializerSettings DtoSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None,
            // Serialize enums as names ("Success", not 0) — external MCP clients read semantic values.
            Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() }
        };

        private readonly IEnvironmentDetector _environment;
        private readonly PackageService _packages;
        private readonly ProjectInspector _projects;
        private readonly IReadOnlyList<ToolDef> _tools;

        public McpServer(IEnvironmentDetector? environment = null, PackageService? packages = null)
        {
            _environment = environment ?? new WindowsEnvironmentDetector();
            _packages = packages ?? new PackageService();
            _projects = new ProjectInspector(_environment, _packages);
            _tools = BuildTools();
        }

        /// <summary>Runs the stdio loop until EOF. One JSON-RPC message per line.</summary>
        public void Run(TextReader input, TextWriter output)
        {
            string? line;
            while ((line = input.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                JObject request;
                try { request = JObject.Parse(line); }
                catch (JsonException) { continue; } // ignore malformed framing

                var response = Dispatch(request);
                if (response != null)
                {
                    output.Write(response.ToString(Formatting.None));
                    output.Write('\n');
                    output.Flush();
                }
            }
        }

        /// <summary>Handles one request. Returns null for notifications (no id).</summary>
        public JObject? Dispatch(JObject request)
        {
            var method = (string?)request["method"];
            var id = request["id"];
            var isNotification = id == null;

            try
            {
                switch (method)
                {
                    case "initialize":
                        return Result(id, new JObject
                        {
                            ["protocolVersion"] = ProtocolVersion,
                            ["capabilities"] = new JObject { ["tools"] = new JObject() },
                            ["serverInfo"] = new JObject { ["name"] = "ssis-agent-mcp", ["version"] = "0.1.0" }
                        });

                    case "notifications/initialized":
                        return null;

                    case "tools/list":
                        return Result(id, new JObject { ["tools"] = ListTools() });

                    case "tools/call":
                        return HandleToolCall(id, (JObject?)request["params"]);

                    default:
                        return isNotification ? null : Error(id, -32601, $"Method not found: {method}");
                }
            }
            catch (Exception ex)
            {
                return isNotification ? null : Error(id, -32603, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private JObject? HandleToolCall(JToken? id, JObject? prms)
        {
            var name = (string?)prms?["name"];
            var args = (JObject?)prms?["arguments"] ?? new JObject();
            var tool = _tools.FirstOrDefaultByName(name);
            if (tool == null) return Error(id, -32602, $"Unknown tool: {name}");

            try
            {
                var dto = tool.Handler(args);
                var json = JsonConvert.SerializeObject(dto, DtoSettings);
                return Result(id, new JObject
                {
                    ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = json } },
                    ["isError"] = false
                });
            }
            catch (Exception ex)
            {
                return Result(id, new JObject
                {
                    ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = ex.GetType().Name + ": " + ex.Message } },
                    ["isError"] = true
                });
            }
        }

        private JArray ListTools()
        {
            var arr = new JArray();
            foreach (var t in _tools)
                arr.Add(new JObject { ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.InputSchema });
            return arr;
        }

        private IReadOnlyList<ToolDef> BuildTools()
        {
            JObject PathSchema(string prop, string desc) => new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject { [prop] = new JObject { ["type"] = "string", ["description"] = desc } },
                ["required"] = new JArray { prop }
            };

            string ReqPath(JObject a, string p)
            {
                var v = (string?)a[p];
                if (string.IsNullOrWhiteSpace(v)) throw new ArgumentException($"'{p}' is required");
                return v!;
            }

            return new List<ToolDef>
            {
                new ToolDef("environment.detect",
                    "Detect the Windows/SSIS/Visual Studio/provider environment.",
                    new JObject { ["type"] = "object", ["properties"] = new JObject() },
                    _ => _environment.Detect()),

                new ToolDef("project.inspect",
                    "Inspect a .dtproj: TargetServerVersion, packages, connections, and a compatibility assessment.",
                    PathSchema("projectPath", "Absolute path to the .dtproj file."),
                    a => _projects.Inspect(ReqPath(a, "projectPath"))),

                new ToolDef("package.inspect",
                    "Inspect a .dtsx: Control Flow, precedence constraints, connections, and Data Flow.",
                    PathSchema("packagePath", "Absolute path to the .dtsx file."),
                    a => _packages.InspectFile(ReqPath(a, "packagePath"))),

                new ToolDef("controlflow.inspect",
                    "Inspect only the Control Flow (executables + precedence constraints) of a .dtsx.",
                    PathSchema("packagePath", "Absolute path to the .dtsx file."),
                    a =>
                    {
                        var info = _packages.InspectFile(ReqPath(a, "packagePath"));
                        return new { name = info.Name, executables = info.Executables, precedenceConstraints = info.PrecedenceConstraints };
                    }),

                new ToolDef("dataflow.inspect",
                    "Inspect only the Data Flow(s) of a .dtsx (components, columns, lineage, paths).",
                    PathSchema("packagePath", "Absolute path to the .dtsx file."),
                    a =>
                    {
                        var info = _packages.InspectFile(ReqPath(a, "packagePath"));
                        return new { name = info.Name, dataFlows = info.DataFlows };
                    }),

                new ToolDef("metadata.inspect",
                    "Inspect the lineage/metadata graph + validation of one Data Flow Task in a .dtsx.",
                    Schema(("packagePath","Absolute path to the .dtsx.",true), ("dataFlowTask","Name of the Data Flow Task.",true)),
                    a => new LineageInspector(_packages).Inspect(ReqPath(a,"packagePath"), ReqPath(a,"dataFlowTask"))),

                new ToolDef("package.create",
                    "Create a new empty .dtsx package (no seeded tasks/connections).",
                    Schema(("packagePath","Absolute path for the new .dtsx.",true), ("name","Package name.",true), ("targetServerVersion","Optional (e.g. 2022).",false)),
                    a =>
                    {
                        var path = ReqPath(a,"packagePath");
                        var pkg = _packages.CreateEmpty(ReqPath(a,"name"), (string?)a["targetServerVersion"]);
                        _packages.Save(pkg, path);
                        return _packages.InspectFile(path);
                    }),

                new ToolDef("controlflow.apply",
                    "Apply (or preview) a batch of Control Flow operations atomically through the Safety layer. " +
                    "operations: addConnection, addVariable, addTask, configureExecuteSql, configureScriptTask, " +
                    "setTaskProperty, connect, disconnect, removeTask, rename. Set preview:true for a validated dry-run (no write).",
                    ApplySchema("Control Flow operation objects, e.g. {op:'addTask',kind:'DataFlow',name:'DFT'}."),
                    a =>
                    {
                        var path = ReqPath(a, "packagePath");
                        var ops = (JArray)(a["operations"] ?? new JArray());
                        var editor = new PackageEditor(_packages);
                        void Act(ControlFlowBuilder b) => OperationTranslator.ApplyControlFlow(b, ops);
                        return Preview(a) ? editor.PreviewControlFlow(path, Act) : editor.Apply(path, Act, "controlflow.apply");
                    }),

                new ToolDef("dataflow.apply",
                    "Apply (or preview) a batch of Data Flow operations to a named Data Flow Task, atomically through the Safety layer. " +
                    "operations: addComponent, configure{OleDb,AdoNet,Excel,FlatFile}{Source,Destination}, connect, exposeAllInputColumns, " +
                    "derivedColumn, dataConversion, conditionalSplitCase, lookup, map, autoMap. Set preview:true for a validated dry-run (no write).",
                    DataFlowApplySchema(),
                    a =>
                    {
                        var path = ReqPath(a, "packagePath");
                        var dft = ReqPath(a, "dataFlowTask");
                        var ops = (JArray)(a["operations"] ?? new JArray());
                        var editor = new PackageEditor(_packages);
                        void Act(DataFlowBuilder b) => OperationTranslator.ApplyDataFlow(b, ops);
                        return Preview(a) ? editor.PreviewDataFlow(path, dft, Act) : editor.ApplyDataFlow(path, dft, Act, "dataflow.apply");
                    }),

                new ToolDef("layout.apply",
                    "Apply the unified Control Flow + Data Flow layout (top→bottom) and persist it in the .dtsx.",
                    Schema(("packagePath","Absolute path to the .dtsx.",true), ("mode","AddMissing (default) or Relayout.",false)),
                    a =>
                    {
                        var path = ReqPath(a,"packagePath");
                        var mode = ParseLayoutMode((string?)a["mode"]);
                        var info = _packages.InspectFile(path);
                        var boxes = new PackageLayoutEngine().Apply(path, info, mode);
                        return new { applied = true, mode = mode.ToString(), nodes = boxes };
                    }),

                new ToolDef("package.validate",
                    "Load a .dtsx and run the real SSIS Package.Validate.",
                    PathSchema("packagePath","Absolute path to the .dtsx."),
                    a =>
                    {
                        var pkg = _packages.Load(ReqPath(a,"packagePath"));
                        var res = _packages.Validate(pkg);
                        return new { result = res.ToString(), valid = res == Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Success };
                    }),

                new ToolDef("package.execute",
                    "Execute a .dtsx with a Microsoft-signed host (licensed dtexec). Returns a structured outcome " +
                    "(Success/Failure/EnvironmentBlocked) with errors/warnings — never faked.",
                    PathSchema("packagePath","Absolute path to the .dtsx."),
                    a => new SsdtDebugExecutionHost().Execute(ReqPath(a,"packagePath"))),

                new ToolDef("package.undo",
                    "Undo the last committed change by restoring the most recent Safety backup, then re-inspect.",
                    PathSchema("packagePath","Absolute path to the .dtsx."),
                    a => new PackageEditor(_packages).Undo(ReqPath(a,"packagePath"))),

                new ToolDef("connection.test",
                    "Test that a named connection manager can connect (AcquireConnection) without running the package.",
                    Schema(("packagePath","Absolute path to the .dtsx.",true), ("connection","Connection manager name.",true)),
                    a => new ConnectionTester(_packages).Test(ReqPath(a,"packagePath"), ReqPath(a,"connection"))),

                new ToolDef("data.verify",
                    "Run a scalar SQL query against a SQL Server connection string and (optionally) compare to an expected value. " +
                    "For business verification of destination data after execution.",
                    Schema(("connectionString","ADO.NET SQL Server connection string.",true), ("sql","Scalar SELECT.",true), ("expected","Optional expected value to compare (string/number).",false)),
                    a =>
                    {
                        var v = new DestinationDataVerifier(ReqPath(a,"connectionString")).Scalar(ReqPath(a,"sql"));
                        var expected = (a["expected"] as Newtonsoft.Json.Linq.JValue)?.Value;
                        bool? matched = expected == null ? (bool?)null : string.Equals(System.Convert.ToString(v), System.Convert.ToString(expected), StringComparison.Ordinal);
                        return new { value = v, expected, matched };
                    }),
            };
        }

        private static bool Preview(JObject a) => a["preview"] != null && (bool)a["preview"]!;

        private static LayoutMode ParseLayoutMode(string? s) =>
            string.Equals(s, "Relayout", StringComparison.OrdinalIgnoreCase) ? LayoutMode.Relayout : LayoutMode.AddMissing;

        private static JObject Schema(params (string name, string desc, bool required)[] props)
        {
            var p = new JObject();
            var req = new JArray();
            foreach (var (name, desc, required) in props)
            {
                p[name] = new JObject { ["type"] = "string", ["description"] = desc };
                if (required) req.Add(name);
            }
            return new JObject { ["type"] = "object", ["properties"] = p, ["required"] = req };
        }

        private static JObject ApplySchema(string opsDesc) => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["packagePath"] = new JObject { ["type"] = "string", ["description"] = "Absolute path to the .dtsx." },
                ["preview"] = new JObject { ["type"] = "boolean", ["description"] = "true = validated dry-run (no write)." },
                ["operations"] = new JObject { ["type"] = "array", ["description"] = opsDesc, ["items"] = new JObject { ["type"] = "object" } }
            },
            ["required"] = new JArray { "packagePath", "operations" }
        };

        private static JObject DataFlowApplySchema()
        {
            var s = ApplySchema("Data Flow operation objects, e.g. {op:'addComponent',kind:'OleDbSource',name:'Src'}.");
            ((JObject)s["properties"]!)["dataFlowTask"] = new JObject { ["type"] = "string", ["description"] = "Name of the target Data Flow Task." };
            ((JArray)s["required"]!).Add("dataFlowTask");
            return s;
        }

        private static JObject Result(JToken? id, JObject result) =>
            new JObject { ["jsonrpc"] = "2.0", ["id"] = id ?? JValue.CreateNull(), ["result"] = result };

        private static JObject Error(JToken? id, int code, string message) =>
            new JObject { ["jsonrpc"] = "2.0", ["id"] = id ?? JValue.CreateNull(),
                ["error"] = new JObject { ["code"] = code, ["message"] = message } };
    }

    internal sealed class ToolDef
    {
        public ToolDef(string name, string description, JObject inputSchema, Func<JObject, object> handler)
        {
            Name = name; Description = description; InputSchema = inputSchema; Handler = handler;
        }
        public string Name { get; }
        public string Description { get; }
        public JObject InputSchema { get; }
        public Func<JObject, object> Handler { get; }
    }

    internal static class ToolDefExtensions
    {
        public static ToolDef? FirstOrDefaultByName(this IReadOnlyList<ToolDef> tools, string? name)
        {
            foreach (var t in tools) if (t.Name == name) return t;
            return null;
        }
    }
}
