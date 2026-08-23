using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SsisMcp.Planner
{
    /// <summary>Explicit phases of the autonomous planner (reported in order).</summary>
    public enum PlannerState
    {
        Discover, Analyze, Plan, Clarify, Ready, Preview, Apply, Validate, Execute, Verify, Repair, Complete, Failed
    }

    /// <summary>Where a decision came from — never blur inferred with explicit.</summary>
    public enum Provenance { Explicit, InferredHigh, InferredLow }

    /// <summary>Provider-agnostic MCP tool access. The planner NEVER touches builders directly.</summary>
    public interface IMcpToolInvoker
    {
        /// <summary>Invoke an MCP tool; return the parsed result DTO. Throw McpToolException on tool error.</summary>
        JToken Invoke(string tool, JObject arguments);
    }

    public sealed class McpToolException : System.Exception
    {
        public McpToolException(string tool, string message) : base($"{tool}: {message}") { Tool = tool; }
        public string Tool { get; }
    }

    // ---- request ----
    public sealed class ConnectionSpec
    {
        public string Name = "";
        public string Kind = "oledb-sql";      // oledb-sql | adonet-sql | excel | access
        public string? DataSource;             // for sql
        public string? Catalog;                // for sql
        public string? FilePath;               // for excel/access
        public bool Xlsx = true;
        public bool Header = true;
    }

    /// <summary>Explicit, client/user-supplied hint. The planner treats these as Explicit provenance.</summary>
    public sealed class MappingHint
    {
        public string TargetTable = "";
        public string? SourceName;                                   // which source object feeds it
        public Dictionary<string, string> ColumnMap = new();         // destCol -> srcCol (override)
        public List<DerivedHint> Derived = new();                    // explicit derived columns
    }
    public sealed class DerivedHint { public string Column = ""; public string Expression = ""; public string DataType = "DT_STR"; public int Length; public int Precision; public int Scale; public int CodePage = 1252; }

    public sealed class PlannerRequest
    {
        public string InputDir = "";
        public string PackagePath = "";
        public string PackageName = "GeneratedPackage";
        public ConnectionSpec Target = new();                        // destination SQL Server
        public string? TargetSchemaSql;                              // .sql defining target tables (else inferred: the 0-insert script)
        public List<ConnectionSpec> Sources = new();                 // source connections to create (endpoints provided, never invented)
        public List<MappingHint> Hints = new();
        public Dictionary<string, string> Answers = new();           // questionId -> answer (resolves ambiguities)
        public bool Execute = true;                                  // run + verify when a licensed host is present
        public int MaxRepairAttempts = 3;
    }

    // ---- plan model ----
    public sealed class ColumnPlan
    {
        public string DestColumn = "";
        public string? SourceColumn;              // buffer column feeding the dest (may be a converted column)
        public string? Conversion;                // e.g. "DT_WSTR->DT_STR" when a Data Conversion was inserted
        public Provenance Provenance = Provenance.InferredHigh;
        public string Note = "";
    }

    public sealed class DftPlan
    {
        public string Name = "";
        public string TargetTable = "";
        public string SourceConnection = "";
        public string SourceKind = "";            // oledb-source | adonet-source | excel-source | access-source
        public string SourceObject = "";          // table / sheet / SQL
        public int SourceRowCount;
        public List<ColumnPlan> Columns = new();
        public List<JObject> DataFlowOps = new(); // the exact dataflow.apply operations
        public Provenance Provenance = Provenance.InferredHigh;
    }

    public sealed class Ambiguity
    {
        public string Id = "";
        public string Question = "";
        public string Context = "";
        public List<string> Options = new();
    }

    public sealed class Plan
    {
        public string PackagePath = "";
        public string PackageName = "";
        public List<JObject> ControlFlowOps = new();   // addConnection/addTask/connect/addVariable
        public List<DftPlan> Dfts = new();
        public List<Ambiguity> Ambiguities = new();
        public List<string> Notes = new();
    }

    // ---- result / report ----
    public sealed class PhaseRecord { public PlannerState State; public string Detail = ""; public bool Ok = true; }
    public sealed class VerifyRecord { public string Target = ""; public long Expected; public long Actual; public bool Matched; }

    public sealed class PlannerResult
    {
        public PlannerState FinalState;
        public bool NeedsClarification => Ambiguities.Count > 0 && FinalState == PlannerState.Clarify;
        public List<PhaseRecord> Phases = new();
        public List<Ambiguity> Ambiguities = new();
        public Plan? Plan;
        public List<VerifyRecord> Verifications = new();
        public List<string> ExplicitDecisions = new();
        public List<string> InferredDecisions = new();
        public List<string> Unresolved = new();
        public int RepairAttempts;
        public string Summary = "";
    }
}
