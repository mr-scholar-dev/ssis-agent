using System;
using System.Collections.Generic;
using SsisMcp.Core.Building;
using Wrapper = Microsoft.SqlServer.Dts.Pipeline.Wrapper;

namespace SsisMcp.Ssis.Building
{
    /// <summary>
    /// Column mapping engine for a destination component: inspect/compare/auto-map/set/remove.
    /// Classifies each candidate (Exact/Compatible/RequiresConversion/Incompatible/MissingSource/
    /// MissingDestination) by name + SSIS data type + length/precision/scale/codepage. Never inserts
    /// conversions silently — RequiresConversion is reported, not auto-fixed.
    /// </summary>
    public sealed class MappingEngine
    {
        private readonly DataFlowBuilder _builder;
        public MappingEngine(DataFlowBuilder builder) => _builder = builder;

        /// <summary>Compares the destination's upstream input columns against its external metadata.</summary>
        public MappingResult Compare(string destinationName)
        {
            var comp = _builder.Require(destinationName);
            var input = comp.InputCollection[0];
            var vInput = input.GetVirtualInput();

            // index external metadata by name
            var ext = new Dictionary<string, Wrapper.IDTSExternalMetadataColumn100>(StringComparer.OrdinalIgnoreCase);
            foreach (Wrapper.IDTSExternalMetadataColumn100 e in input.ExternalMetadataColumnCollection)
                ext[e.Name] = e;

            var result = new MappingResult { Component = destinationName };
            var matchedExternal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Wrapper.IDTSVirtualInputColumn100 v in vInput.VirtualInputColumnCollection)
            {
                var m = new ColumnMappingInfo
                {
                    SourceColumn = v.Name,
                    SourceLineageId = v.LineageID,
                    SourceDataType = v.DataType.ToString()
                };
                if (ext.TryGetValue(v.Name, out var e))
                {
                    matchedExternal.Add(v.Name);
                    m.DestinationColumn = e.Name;
                    m.DestinationDataType = e.DataType.ToString();
                    m.Classification = Classify(v, e);
                }
                else m.Classification = MappingClassification.MissingDestination;
                result.Mappings.Add(m);
            }

            foreach (Wrapper.IDTSExternalMetadataColumn100 e in input.ExternalMetadataColumnCollection)
                if (!matchedExternal.Contains(e.Name))
                    result.Mappings.Add(new ColumnMappingInfo
                    {
                        DestinationColumn = e.Name,
                        DestinationDataType = e.DataType.ToString(),
                        Classification = MappingClassification.MissingSource
                    });

            return result;
        }

        /// <summary>Maps every Exact/Compatible pair by name. Returns the classification report.
        /// RequiresConversion/Incompatible pairs are left unmapped (never silently converted).</summary>
        public MappingResult AutoMap(string destinationName)
        {
            var report = Compare(destinationName);
            foreach (var m in report.Mappings)
            {
                if (m.SourceColumn == null || m.DestinationColumn == null) continue;
                if (m.Classification != MappingClassification.Exact && m.Classification != MappingClassification.Compatible)
                    continue;
                // Re-fetch metadata per column: mapping one column mutates the input's virtual
                // input, so a captured reference would go stale and MapInputColumn would throw.
                SetMapping(destinationName, m.SourceColumn!, m.DestinationColumn!);
            }
            return report;
        }

        /// <summary>Maps a single source column to a destination external column by name.</summary>
        public void SetMapping(string destinationName, string sourceColumn, string destinationColumn)
        {
            var comp = _builder.Require(destinationName);
            var inst = comp.Instantiate();
            var input = comp.InputCollection[0];
            var vInput = input.GetVirtualInput();
            var lineage = FindVirtualLineage(vInput, sourceColumn);
            inst.SetUsageType(input.ID, vInput, lineage, Wrapper.DTSUsageType.UT_READONLY);
            var ext = FindExternal(input, destinationColumn);
            var inputCol = FindInputColumn(input, lineage);
            inst.MapInputColumn(input.ID, inputCol.ID, ext.ID);
        }

        /// <summary>Unmaps a destination external column (sets the input column usage to none).</summary>
        public void RemoveMapping(string destinationName, string sourceColumn)
        {
            var comp = _builder.Require(destinationName);
            var inst = comp.Instantiate();
            var input = comp.InputCollection[0];
            var vInput = input.GetVirtualInput();
            var lineage = FindVirtualLineage(vInput, sourceColumn);
            inst.SetUsageType(input.ID, vInput, lineage, Wrapper.DTSUsageType.UT_IGNORED);
        }

        private static MappingClassification Classify(Wrapper.IDTSVirtualInputColumn100 src, Wrapper.IDTSExternalMetadataColumn100 dst)
        {
            if (src.DataType != dst.DataType) return MappingClassification.RequiresConversion;
            var sameSize = src.Length == dst.Length && src.Precision == dst.Precision
                           && src.Scale == dst.Scale && src.CodePage == dst.CodePage;
            return sameSize ? MappingClassification.Exact : MappingClassification.Compatible;
        }

        private static Wrapper.IDTSExternalMetadataColumn100 FindExternal(Wrapper.IDTSInput100 input, string name)
        {
            foreach (Wrapper.IDTSExternalMetadataColumn100 e in input.ExternalMetadataColumnCollection)
                if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return e;
            throw new BuilderException(BuilderErrorCode.MissingDestination, $"external column '{name}' not found");
        }

        private static Wrapper.IDTSInputColumn100 FindInputColumn(Wrapper.IDTSInput100 input, int lineage)
        {
            foreach (Wrapper.IDTSInputColumn100 c in input.InputColumnCollection)
                if (c.LineageID == lineage) return c;
            throw new BuilderException(BuilderErrorCode.InvalidLineageState, $"input column lineage {lineage} not found");
        }

        private static int FindVirtualLineage(Wrapper.IDTSVirtualInput100 vInput, string name)
        {
            foreach (Wrapper.IDTSVirtualInputColumn100 v in vInput.VirtualInputColumnCollection)
                if (string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)) return v.LineageID;
            throw new BuilderException(BuilderErrorCode.MissingSource, $"source column '{name}' not found");
        }
    }
}
