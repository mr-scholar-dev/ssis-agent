using System.Collections.Generic;

namespace SsisMcp.Core.Building
{
    /// <summary>Classification of a source→destination column mapping candidate.</summary>
    public enum MappingClassification
    {
        Exact,
        Compatible,
        RequiresConversion,
        Incompatible,
        MissingSource,
        MissingDestination
    }

    /// <summary>One evaluated source→destination column pairing produced by the mapping engine.</summary>
    public sealed class ColumnMappingInfo
    {
        public string? SourceColumn { get; set; }
        public int SourceLineageId { get; set; }
        public string? DestinationColumn { get; set; }
        public string? SourceDataType { get; set; }
        public string? DestinationDataType { get; set; }
        public MappingClassification Classification { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>Result of inspecting/comparing/auto-mapping a destination's columns against upstream.</summary>
    public sealed class MappingResult
    {
        public string Component { get; set; } = "";
        public List<ColumnMappingInfo> Mappings { get; } = new List<ColumnMappingInfo>();
    }
}
