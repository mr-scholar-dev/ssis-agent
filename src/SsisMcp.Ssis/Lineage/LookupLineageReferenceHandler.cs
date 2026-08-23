using System.Collections.Generic;
using SsisMcp.Core.Lineage;
using Wrapper = Microsoft.SqlServer.Dts.Pipeline.Wrapper;

namespace SsisMcp.Ssis.Lineage
{
    /// <summary>
    /// Lookup handler. Empirically, Lookup keeps its references by NAME, not by numeric lineage:
    /// join columns via the input column custom property <c>JoinToReferenceColumn</c> and returned
    /// columns via the output column custom property <c>CopyFromReferenceColumn</c>. Name-based
    /// references survive lineage reassignment on reload, so Lookup does not exhibit the Data
    /// Conversion stale-lineage bug. This handler still validates structural integrity: every join
    /// input column must resolve to a current input column, and every returned column must name a
    /// reference column. There is nothing numeric to rebind (reported as such).
    /// </summary>
    public sealed class LookupLineageReferenceHandler : ILineageReferenceHandler
    {
        private const string Join = "JoinToReferenceColumn";
        private const string Copy = "CopyFromReferenceColumn";

        public bool Handles(Wrapper.IDTSComponentMetaData100 component)
        {
            var classId = component.ComponentClassID ?? "";
            if (classId.IndexOf("Lookup", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // structural signature: an output named "Lookup Match Output"
            foreach (Wrapper.IDTSOutput100 o in component.OutputCollection)
                if (o.Name == "Lookup Match Output") return true;
            return false;
        }

        public IEnumerable<StaleLineageReference> FindStale(Wrapper.IDTSComponentMetaData100 component)
        {
            // Join columns must resolve to a live input column (they are set on input columns, so
            // they cannot dangle by lineage). A returned column missing CopyFromReferenceColumn is a
            // structural defect worth surfacing.
            foreach (Wrapper.IDTSOutput100 o in component.OutputCollection)
                foreach (Wrapper.IDTSOutputColumn100 col in o.OutputColumnCollection)
                {
                    var hasCopy = false;
                    foreach (Wrapper.IDTSCustomProperty100 p in col.CustomPropertyCollection)
                        if (p.Name == Copy && p.Value != null && !string.IsNullOrEmpty(p.Value.ToString())) hasCopy = true;
                    // Only flag columns that look like returned columns but lost their binding.
                    if (o.Name == "Lookup Match Output" && ColumnIsReturned(col) && !hasCopy)
                        yield return new StaleLineageReference
                        {
                            Component = component.Name,
                            Carrier = $"{col.Name}.{Copy}",
                            ReferencedLineageId = 0,
                            Reason = "returned column lost its CopyFromReferenceColumn binding"
                        };
                }
        }

        // Name-based references: nothing numeric to rebind. Reported explicitly.
        public IEnumerable<RepairAction> Rebind(Wrapper.IDTSComponentMetaData100 component, RepairMode mode)
            => System.Array.Empty<RepairAction>();

        private static bool ColumnIsReturned(Wrapper.IDTSOutputColumn100 col)
        {
            foreach (Wrapper.IDTSCustomProperty100 p in col.CustomPropertyCollection)
                if (p.Name == Copy) return true;
            return false;
        }
    }
}
