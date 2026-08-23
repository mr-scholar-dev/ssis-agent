using System.Collections.Generic;

namespace SsisMcp.Core.Lineage
{
    /// <summary>Confidence that a proposed lineage repair is correct.</summary>
    public enum RepairConfidence
    {
        /// <summary>Unique match on stable identity (component/port/name/type). Safe to apply.</summary>
        Exact,
        /// <summary>Single type-compatible candidate; safe but not name-identical.</summary>
        Compatible,
        /// <summary>More than one candidate — must NOT be applied automatically.</summary>
        Ambiguous,
        /// <summary>No candidate found — manual intervention required.</summary>
        None
    }

    /// <summary>How aggressively the engine repairs.</summary>
    public enum RepairMode
    {
        /// <summary>Detect and report only; never mutate.</summary>
        DiagnoseOnly,
        /// <summary>Apply only Exact/Compatible non-ambiguous repairs.</summary>
        SafeRepair,
        /// <summary>Internal only (not exposed): also apply best-guess ambiguous repairs.</summary>
        ForceRepair
    }

    /// <summary>A lineage reference (usually a custom property) that no longer resolves after reload.</summary>
    public sealed class StaleLineageReference
    {
        public string Component { get; set; } = "";
        public string Carrier { get; set; } = "";           // e.g. output column "NameW" / SourceInputColumnLineageID
        public int ReferencedLineageId { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>A concrete repair the engine performed or proposes.</summary>
    public sealed class RepairAction
    {
        public string RepairType { get; set; } = "";        // e.g. RebindInputColumnLineage
        public string Component { get; set; } = "";
        public string OldReference { get; set; } = "";
        public string NewReference { get; set; } = "";
        public RepairConfidence Confidence { get; set; }
        public string Reason { get; set; } = "";
        public bool Applied { get; set; }
    }

    /// <summary>Result of validating lineage + external metadata for a data flow.</summary>
    public sealed class LineageValidationResult
    {
        public bool IsValid => Stale.Count == 0 && ExternalMetadataIssues.Count == 0;
        public List<StaleLineageReference> Stale { get; } = new List<StaleLineageReference>();
        public List<string> ExternalMetadataIssues { get; } = new List<string>();
    }

    /// <summary>Outcome of a bounded repair loop.</summary>
    public sealed class LineageRepairReport
    {
        public int Passes { get; set; }
        public List<RepairAction> Actions { get; } = new List<RepairAction>();
        public bool FinalValid { get; set; }
        public bool ManualInterventionRequired { get; set; }
        public string? Detail { get; set; }
    }
}
