using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SsisMcp.Core.Building;
using SsisMcp.Ssis.Building;
using Rt = Microsoft.SqlServer.Dts.Runtime.Wrapper;

namespace SsisMcp.Server
{
    /// <summary>
    /// Translates the composable MCP operation DSL (a JSON array of {op, ...} objects) into calls on
    /// the internal builders. One batch = one atomic pass through the Safety layer, so a client
    /// composes many primitives without a tool-per-component explosion. Unknown ops / bad args throw
    /// BuilderException so the caller gets a structured code, not a raw crash.
    /// </summary>
    internal static class OperationTranslator
    {
        // ---------------- Control Flow ----------------
        public static void ApplyControlFlow(ControlFlowBuilder b, JArray ops)
        {
            foreach (var t in ops)
            {
                var o = (JObject)t;
                switch (Op(o))
                {
                    case "addconnection":
                        b.AddConnection(Str(o, "kind"), Str(o, "name"),
                            OptStr(o, "dataSource"), OptStr(o, "catalog"), OptStr(o, "filePath"),
                            OptBool(o, "xlsx", true), OptBool(o, "hdr", true));
                        break;
                    case "addvariable":
                        b.AddVariable(Str(o, "name"), OptStr(o, "namespace") ?? "User", OptObj(o, "value"), OptBool(o, "readOnly", false));
                        break;
                    case "addtask":
                        b.AddTask(Str(o, "kind"), Str(o, "name"), OptStr(o, "parent"));
                        break;
                    case "configureexecutesql":
                        b.ConfigureExecuteSql(Str(o, "name"), OptStr(o, "connection"), OptStr(o, "sql"),
                            OptInt(o, "resultSetType"), OptInt(o, "sqlSourceType"), OptBoolN(o, "bypassPrepare"), OptInt(o, "timeoutSeconds"));
                        break;
                    case "configurescripttask":
                        b.ConfigureScriptTask(Str(o, "name"), Str(o, "source"),
                            StrArr(o, "readOnlyVariables"), StrArr(o, "readWriteVariables"), StrArr(o, "references"),
                            OptStr(o, "entryPoint") ?? "Main");
                        break;
                    case "settaskproperty":
                        b.SetTaskProperty(Str(o, "name"), Str(o, "property"), OptObj(o, "value") ?? "");
                        break;
                    case "connect":
                        b.Connect(Str(o, "from"), Str(o, "to"),
                            ParseEnum(OptStr(o, "value"), PrecedenceValue.Success),
                            ParseEnum(OptStr(o, "evalOp"), PrecedenceEval.Constraint),
                            OptStr(o, "expression"));
                        break;
                    case "disconnect":
                        b.Disconnect(Str(o, "from"), Str(o, "to"));
                        break;
                    case "removetask":
                        b.RemoveTask(Str(o, "name"), OptBool(o, "force", false));
                        break;
                    case "rename":
                        b.RenameTask(Str(o, "old"), Str(o, "new"));
                        break;
                    default:
                        throw new BuilderException(BuilderErrorCode.Unsupported, $"unknown control-flow op '{Op(o)}'");
                }
            }
        }

        // ---------------- Data Flow ----------------
        public static void ApplyDataFlow(DataFlowBuilder b, JArray ops)
        {
            var mapping = new MappingEngine(b);
            foreach (var t in ops)
            {
                var o = (JObject)t;
                switch (Op(o))
                {
                    case "addcomponent":
                        b.AddComponent(Str(o, "kind"), Str(o, "name"));
                        break;
                    case "configureoledbsource":
                        b.ConfigureOleDbSource(Str(o, "name"), Str(o, "connection"), Int(o, "accessMode"), Str(o, "sqlOrTable"));
                        break;
                    case "configureoledbdestination":
                        b.ConfigureOleDbDestination(Str(o, "name"), Str(o, "connection"), Str(o, "table"),
                            OptInt(o, "accessMode") ?? 0, OptBool(o, "keepIdentity", false));
                        break;
                    case "configureadonetsource":
                        b.ConfigureAdoNetSource(Str(o, "name"), Str(o, "connection"), Int(o, "accessMode"), Str(o, "sqlOrTable"));
                        break;
                    case "configureadonetdestination":
                        b.ConfigureAdoNetDestination(Str(o, "name"), Str(o, "connection"), Str(o, "table"));
                        break;
                    case "configureexcelsource":
                        b.ConfigureExcelSource(Str(o, "name"), Str(o, "connection"), Str(o, "sheet"));
                        break;
                    case "configureexceldestination":
                        b.ConfigureExcelDestination(Str(o, "name"), Str(o, "connection"), Str(o, "sheet"));
                        break;
                    case "configureflatfilesource":
                        b.ConfigureFlatFileSource(Str(o, "name"), Str(o, "connection"));
                        break;
                    case "configureflatfiledestination":
                        b.ConfigureFlatFileDestination(Str(o, "name"), Str(o, "connection"), OptBool(o, "overwrite", true));
                        break;
                    case "connect":
                        b.Connect(Str(o, "from"), Str(o, "to"), OptStr(o, "fromOutput"), OptStr(o, "toInput"));
                        break;
                    case "exposeallinputcolumns":
                        b.ExposeAllInputColumns(Str(o, "name"));
                        break;
                    case "derivedcolumn":
                        b.ConfigureDerivedColumn(Str(o, "name"), Str(o, "columnName"), Str(o, "expression"),
                            Dt(o, "dataType"), OptInt(o, "length") ?? 0, OptInt(o, "precision") ?? 0, OptInt(o, "scale") ?? 0, OptInt(o, "codePage") ?? 0);
                        break;
                    case "dataconversion":
                        b.ConfigureDataConversion(Str(o, "name"), Str(o, "inputColumn"), Str(o, "columnName"),
                            Dt(o, "dataType"), OptInt(o, "length") ?? 0, OptInt(o, "precision") ?? 0, OptInt(o, "scale") ?? 0, OptInt(o, "codePage") ?? 0);
                        break;
                    case "conditionalsplitcase":
                        b.AddConditionalSplitCase(Str(o, "name"), Str(o, "outputName"), Str(o, "expression"), Int(o, "evaluationOrder"));
                        break;
                    case "lookup":
                        b.ConfigureLookup(Str(o, "name"), Str(o, "connection"), Str(o, "referenceSql"),
                            ParseJoins(o), ParseReturnColumns(o),
                            OptInt(o, "cacheType") ?? 0, OptInt(o, "noMatchBehavior") ?? 1);
                        break;
                    case "map":
                        mapping.SetMapping(Str(o, "destination"), Str(o, "sourceColumn"), Str(o, "destinationColumn"));
                        break;
                    case "automap":
                        mapping.AutoMap(Str(o, "destination"));
                        break;
                    default:
                        throw new BuilderException(BuilderErrorCode.Unsupported, $"unknown data-flow op '{Op(o)}'");
                }
            }
        }

        private static IEnumerable<(string, string)> ParseJoins(JObject o)
        {
            var arr = (JArray?)o["joins"] ?? new JArray();
            return arr.Select(j => (Str((JObject)j, "input"), Str((JObject)j, "reference"))).ToList();
        }

        private static IEnumerable<(string, string, Rt.DataType, int, int, int, int)> ParseReturnColumns(JObject o)
        {
            var arr = (JArray?)o["returnColumns"] ?? new JArray();
            return arr.Select(j =>
            {
                var c = (JObject)j;
                return (Str(c, "reference"), Str(c, "alias"), Dt(c, "dataType"),
                    OptInt(c, "length") ?? 0, OptInt(c, "precision") ?? 0, OptInt(c, "scale") ?? 0, OptInt(c, "codePage") ?? 0);
            }).ToList();
        }

        // ---------------- arg helpers ----------------
        private static string Op(JObject o) => (Str(o, "op")).ToLowerInvariant();
        private static string Str(JObject o, string k) => (string?)o[k]
            ?? throw new BuilderException(BuilderErrorCode.InvalidPrecedence, $"'{k}' is required");
        private static string? OptStr(JObject o, string k) => (string?)o[k];
        private static int Int(JObject o, string k) => (int?)o[k]
            ?? throw new BuilderException(BuilderErrorCode.InvalidPrecedence, $"'{k}' (int) is required");
        private static int? OptInt(JObject o, string k) => o[k] == null ? (int?)null : (int)o[k]!;
        private static bool OptBool(JObject o, string k, bool d) => o[k] == null ? d : (bool)o[k]!;
        private static bool? OptBoolN(JObject o, string k) => o[k] == null ? (bool?)null : (bool)o[k]!;
        private static object? OptObj(JObject o, string k) => (o[k] as JValue)?.Value;
        private static string[] StrArr(JObject o, string k) =>
            ((JArray?)o[k])?.Select(x => (string)x!).ToArray() ?? Array.Empty<string>();
        private static Rt.DataType Dt(JObject o, string k)
        {
            var s = Str(o, k);
            if (Enum.TryParse<Rt.DataType>(s, ignoreCase: true, out var dt)) return dt;
            throw new BuilderException(BuilderErrorCode.IncompatibleType, $"unknown SSIS data type '{s}'");
        }
        private static TEnum ParseEnum<TEnum>(string? s, TEnum d) where TEnum : struct =>
            string.IsNullOrWhiteSpace(s) ? d : (Enum.TryParse<TEnum>(s, true, out var v) ? v : d);
    }
}
