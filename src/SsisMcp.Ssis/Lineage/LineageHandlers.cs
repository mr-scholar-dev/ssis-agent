using System.Collections.Generic;
using SsisMcp.Core.Lineage;
using Wrapper = Microsoft.SqlServer.Dts.Pipeline.Wrapper;

namespace SsisMcp.Ssis.Lineage
{
    /// <summary>
    /// Per-component-family handler for lineage references that live OUTSIDE
    /// <c>InputColumn.LineageID</c> (typically in custom properties), which SSIS may not update when
    /// it reassigns lineage IDs on reload. Requirement #6: not every reference is an input column.
    /// </summary>
    public interface ILineageReferenceHandler
    {
        /// <summary>True if this handler recognizes the component. Detection is by structural
        /// signature (custom properties), not just ComponentClassID, because SSIS may persist the
        /// class id as a CLSID after reload rather than the creation moniker.</summary>
        bool Handles(Wrapper.IDTSComponentMetaData100 component);

        /// <summary>Reports references on the component that no longer resolve to a current column.</summary>
        IEnumerable<StaleLineageReference> FindStale(Wrapper.IDTSComponentMetaData100 component);

        /// <summary>Attempts to rebind this component's stale references using stable identity.</summary>
        IEnumerable<RepairAction> Rebind(Wrapper.IDTSComponentMetaData100 component, RepairMode mode);
    }

    /// <summary>
    /// Data Conversion: each output column carries the custom property
    /// <c>SourceInputColumnLineageID</c> pointing at the input column it converts. On reload SSIS
    /// reassigns lineage IDs, dangling this value. Rebind by stable identity (the component's input
    /// columns). A unique input column ⇒ Exact; several ⇒ Ambiguous (never auto-applied).
    /// </summary>
    public sealed class DataConversionLineageHandler : ILineageReferenceHandler
    {
        private const string Prop = "SourceInputColumnLineageID";

        public bool Handles(Wrapper.IDTSComponentMetaData100 component)
        {
            var classId = component.ComponentClassID ?? "";
            if (classId.IndexOf("DataConvert", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // structural signature: an output column carrying SourceInputColumnLineageID
            foreach (Wrapper.IDTSOutput100 o in component.OutputCollection)
                foreach (Wrapper.IDTSOutputColumn100 col in o.OutputColumnCollection)
                    foreach (Wrapper.IDTSCustomProperty100 p in col.CustomPropertyCollection)
                        if (p.Name == Prop) return true;
            return false;
        }

        public IEnumerable<StaleLineageReference> FindStale(Wrapper.IDTSComponentMetaData100 component)
        {
            var valid = CurrentInputLineages(component);
            foreach (Wrapper.IDTSOutput100 output in component.OutputCollection)
                foreach (Wrapper.IDTSOutputColumn100 col in output.OutputColumnCollection)
                {
                    var value = GetProp(col);
                    if (value.HasValue && !valid.Contains(value.Value))
                        yield return new StaleLineageReference
                        {
                            Component = component.Name,
                            Carrier = $"{col.Name}.{Prop}",
                            ReferencedLineageId = value.Value,
                            Reason = $"SourceInputColumnLineageID {value.Value} does not match any current input column"
                        };
                }
        }

        public IEnumerable<RepairAction> Rebind(Wrapper.IDTSComponentMetaData100 component, RepairMode mode)
        {
            var actions = new List<RepairAction>();
            if (component.InputCollection.Count == 0) return actions;
            var input = component.InputCollection[0];
            var inputLineages = new List<int>();
            foreach (Wrapper.IDTSInputColumn100 ic in input.InputColumnCollection) inputLineages.Add(ic.LineageID);
            var valid = new HashSet<int>(inputLineages);

            foreach (Wrapper.IDTSOutput100 output in component.OutputCollection)
                foreach (Wrapper.IDTSOutputColumn100 col in output.OutputColumnCollection)
                {
                    var value = GetProp(col);
                    if (!value.HasValue || valid.Contains(value.Value)) continue; // fine

                    var action = new RepairAction
                    {
                        RepairType = "RebindInputColumnLineage",
                        Component = component.Name,
                        OldReference = $"{col.Name}.{Prop}={value.Value}"
                    };

                    if (inputLineages.Count == 1)
                    {
                        action.Confidence = RepairConfidence.Exact;
                        action.NewReference = $"{col.Name}.{Prop}={inputLineages[0]}";
                        action.Reason = "unique input column on the component";
                        if (mode != RepairMode.DiagnoseOnly) { SetProp(col, inputLineages[0]); action.Applied = true; }
                    }
                    else if (inputLineages.Count == 0)
                    {
                        action.Confidence = RepairConfidence.None;
                        action.Reason = "no input columns to bind to (upstream path removed?)";
                    }
                    else
                    {
                        action.Confidence = RepairConfidence.Ambiguous;
                        action.Reason = $"{inputLineages.Count} input columns — cannot disambiguate safely";
                        if (mode == RepairMode.ForceRepair) { SetProp(col, inputLineages[0]); action.Applied = true; }
                    }
                    actions.Add(action);
                }
            return actions;
        }

        private static HashSet<int> CurrentInputLineages(Wrapper.IDTSComponentMetaData100 component)
        {
            var set = new HashSet<int>();
            foreach (Wrapper.IDTSInput100 input in component.InputCollection)
                foreach (Wrapper.IDTSInputColumn100 ic in input.InputColumnCollection)
                    set.Add(ic.LineageID);
            return set;
        }

        private static int? GetProp(Wrapper.IDTSOutputColumn100 col)
        {
            foreach (Wrapper.IDTSCustomProperty100 p in col.CustomPropertyCollection)
                if (p.Name == Prop && p.Value != null)
                    return System.Convert.ToInt32(p.Value);
            return null;
        }

        private static void SetProp(Wrapper.IDTSOutputColumn100 col, int value)
        {
            foreach (Wrapper.IDTSCustomProperty100 p in col.CustomPropertyCollection)
                if (p.Name == Prop) { p.Value = value; return; }
        }
    }
}
