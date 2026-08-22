using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using SsisMcp.Core.Environment;
using SsisMcp.Ssis;
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
            Formatting = Formatting.None
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
            };
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
