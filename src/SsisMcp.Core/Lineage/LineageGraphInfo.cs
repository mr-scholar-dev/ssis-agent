using System.Collections.Generic;

namespace SsisMcp.Core.Lineage
{
    /// <summary>Serializable lineage graph for a data flow. Backs <c>lineage.inspect</c>/<c>metadata.inspect</c>.</summary>
    public sealed class LineageGraphInfo
    {
        public string DataFlowTask { get; set; } = "";
        public List<LineageColumnNode> Columns { get; } = new List<LineageColumnNode>();
        public List<LineageEdge> Edges { get; } = new List<LineageEdge>();
    }

    /// <summary>An output column, keyed by its current runtime lineage id.</summary>
    public sealed class LineageColumnNode
    {
        public int LineageId { get; set; }
        public string Column { get; set; } = "";
        public string ProducingComponent { get; set; } = "";
        public string? DataType { get; set; }
    }

    /// <summary>An upstream→downstream edge: a downstream input column that consumes a lineage id.</summary>
    public sealed class LineageEdge
    {
        public int LineageId { get; set; }                 // the upstream output column lineage consumed
        public string ConsumingComponent { get; set; } = "";
        public string ConsumingInput { get; set; } = "";
        public bool Orphaned { get; set; }                 // the consumed lineage no longer exists upstream
    }
}
