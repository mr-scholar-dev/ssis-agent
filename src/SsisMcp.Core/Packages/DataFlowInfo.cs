using System.Collections.Generic;

namespace SsisMcp.Core.Packages
{
    /// <summary>A Data Flow Task's pipeline: components + the paths connecting them.</summary>
    public sealed class DataFlowInfo
    {
        /// <summary>Name of the hosting Data Flow Task in the Control Flow.</summary>
        public string TaskName { get; set; } = "";
        public List<ComponentInfo> Components { get; } = new List<ComponentInfo>();
        public List<PathInfo> Paths { get; } = new List<PathInfo>();
    }

    /// <summary>A Data Flow component (source, transformation, or destination).</summary>
    public sealed class ComponentInfo
    {
        public string Name { get; set; } = "";
        public int Id { get; set; }

        /// <summary>Component moniker/class id, e.g. "Microsoft.OLEDBSource" or a CLSID.</summary>
        public string? ComponentClassId { get; set; }

        /// <summary>Rough role inferred from inputs/outputs: source / transformation / destination.</summary>
        public string? Role { get; set; }

        /// <summary>Connection manager names referenced by this component's runtime connections.</summary>
        public List<string> ConnectionManagers { get; } = new List<string>();

        public List<InputOutputInfo> Inputs { get; } = new List<InputOutputInfo>();
        public List<InputOutputInfo> Outputs { get; } = new List<InputOutputInfo>();
    }

    /// <summary>An input or output on a component.</summary>
    public sealed class InputOutputInfo
    {
        public string Name { get; set; } = "";
        public int Id { get; set; }
        public bool IsErrorOut { get; set; }

        public List<ColumnInfo> Columns { get; } = new List<ColumnInfo>();

        /// <summary>External metadata columns (the source/destination schema image), when present.</summary>
        public List<ExternalColumnInfo> ExternalMetadataColumns { get; } = new List<ExternalColumnInfo>();
    }

    /// <summary>A pipeline column with its lineage id and SSIS data type.</summary>
    public sealed class ColumnInfo
    {
        public string Name { get; set; } = "";
        public int LineageId { get; set; }
        public string? DataType { get; set; }
        public int Length { get; set; }
        public int Precision { get; set; }
        public int Scale { get; set; }
        public int CodePage { get; set; }
    }

    /// <summary>An external metadata column (schema image of the underlying source/destination).</summary>
    public sealed class ExternalColumnInfo
    {
        public string Name { get; set; } = "";
        public int Id { get; set; }
        public string? DataType { get; set; }
        public int Length { get; set; }
        public int Precision { get; set; }
        public int Scale { get; set; }
        public int CodePage { get; set; }
    }

    /// <summary>A path connecting one component's output to another component's input.</summary>
    public sealed class PathInfo
    {
        public string Name { get; set; } = "";
        public int Id { get; set; }
        public string? StartComponent { get; set; }
        public string? StartOutput { get; set; }
        public string? EndComponent { get; set; }
        public string? EndInput { get; set; }
    }
}
