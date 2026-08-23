using System;
using System.Collections.Generic;
using SsisMcp.Core.Building;
using SsisMcp.Core.Packages;
using Dts = Microsoft.SqlServer.Dts.Runtime;
using Wrapper = Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using Rt = Microsoft.SqlServer.Dts.Runtime.Wrapper;

namespace SsisMcp.Ssis.Building
{
    /// <summary>
    /// Mutates the pipeline (<see cref="Wrapper.MainPipe"/>) of a Data Flow Task using the real SSIS
    /// Pipeline design-time API. Never writes the .dtsx directly — runs inside a Safety transaction.
    ///
    /// Component lifecycle (empirically confirmed on the v17 runtime):
    ///   New() → ComponentClassID → Instantiate() → ProvideComponentProperties()
    ///   → wire RuntimeConnection → AcquireConnections() → ReinitializeMetaData() → ReleaseConnections()
    ///   → configure columns/properties → attach paths → validate
    /// </summary>
    public sealed class DataFlowBuilder
    {
        private readonly Wrapper.MainPipe _pipe;
        private readonly Dts.Package _pkg;
        private readonly ISsisPipelineComponentCatalog _catalog;

        public DataFlowBuilder(Wrapper.MainPipe pipe, Dts.Package pkg, ISsisPipelineComponentCatalog? catalog = null)
        {
            _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
            _pkg = pkg ?? throw new ArgumentNullException(nameof(pkg));
            _catalog = catalog ?? new SsisPipelineComponentCatalog();
        }

        // ---------------- component lifecycle ----------------

        /// <summary>Adds a component of the logical kind and returns its stable ID.</summary>
        public int AddComponent(string logicalKey, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new BuilderException(BuilderErrorCode.MutationError, "component name is required");
            if (!_catalog.TryResolve(logicalKey, out var classId))
                throw new BuilderException(BuilderErrorCode.Unsupported, $"unknown component kind '{logicalKey}'");
            if (FindOrNull(name) != null)
                throw new BuilderException(BuilderErrorCode.NameCollision, $"a component named '{name}' already exists");

            var comp = _pipe.ComponentMetaDataCollection.New();
            comp.ComponentClassID = classId;
            Wrapper.IDTSDesigntimeComponent100 inst;
            try
            {
                inst = comp.Instantiate();
                inst.ProvideComponentProperties();
            }
            catch (Exception ex)
            {
                _pipe.ComponentMetaDataCollection.RemoveObjectByID(comp.ID);
                throw new BuilderException(BuilderErrorCode.Unsupported,
                    $"runtime cannot create '{logicalKey}' ({classId}): {ex.GetType().Name}: {ex.Message}");
            }
            comp.Name = name;
            return comp.ID;
        }

        public void RenameComponent(string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName))
                throw new BuilderException(BuilderErrorCode.MutationError, "new name is required");
            var comp = Require(oldName);
            if (!string.Equals(oldName, newName, StringComparison.Ordinal) && FindOrNull(newName) != null)
                throw new BuilderException(BuilderErrorCode.NameCollision, $"a component named '{newName}' already exists");
            comp.Name = newName;
        }

        public void RemoveComponent(string name, bool force = false)
        {
            var comp = Require(name);
            var attached = new List<int>();
            foreach (Wrapper.IDTSPath100 p in _pipe.PathCollection)
                if (BelongsTo(p, comp)) attached.Add(p.ID);

            if (attached.Count > 0 && !force)
                throw new BuilderException(BuilderErrorCode.HasDependents,
                    $"component '{name}' has {attached.Count} attached path(s); disconnect them first or force");

            foreach (var id in attached) _pipe.PathCollection.RemoveObjectByID(id);
            _pipe.ComponentMetaDataCollection.RemoveObjectByID(comp.ID);
        }

        // ---------------- paths (explicit ports) ----------------

        /// <summary>Connects a component output to another component input. Ports are explicit.</summary>
        public int Connect(string fromName, string toName,
            string? fromOutput = null, string? toInput = null, bool useErrorOutput = false)
        {
            var from = Require(fromName);
            var to = Require(toName);
            var output = ResolveOutput(from, fromOutput, useErrorOutput);
            var input = ResolveInput(to, toInput);

            foreach (Wrapper.IDTSPath100 existing in _pipe.PathCollection)
                if (existing.StartPoint != null && existing.StartPoint.ID == output.ID &&
                    existing.EndPoint != null && existing.EndPoint.ID == input.ID)
                    throw new BuilderException(BuilderErrorCode.InvalidPrecedence, $"'{fromName}' -> '{toName}' already connected");

            var path = _pipe.PathCollection.New();
            path.AttachPathAndPropagateNotifications(output, input);
            return path.ID;
        }

        public void Disconnect(string fromName, string toName)
        {
            var from = Require(fromName);
            var to = Require(toName);
            foreach (Wrapper.IDTSPath100 p in _pipe.PathCollection)
                if (p.StartPoint != null && p.EndPoint != null &&
                    ComponentOf(p.StartPoint.ID) == from.ID && ComponentOfInput(p.EndPoint.ID) == to.ID)
                {
                    _pipe.PathCollection.RemoveObjectByID(p.ID);
                    return;
                }
            throw new BuilderException(BuilderErrorCode.InvalidPrecedence, $"no path '{fromName}' -> '{toName}' exists");
        }

        // ---------------- source / destination ----------------

        /// <summary>OLE DB Source: access mode 2 = SQL command, 0 = table/view (OpenRowset).</summary>
        public void ConfigureOleDbSource(string name, string connectionManager, int accessMode, string sqlOrTable)
        {
            var comp = Require(name);
            var inst = comp.Instantiate();
            WireConnection(comp, connectionManager);
            inst.SetComponentProperty("AccessMode", accessMode);
            inst.SetComponentProperty(accessMode == 0 ? "OpenRowset" : "SqlCommand", sqlOrTable);
            ReinitUnderConnection(inst);
        }

        /// <summary>OLE DB Destination: access mode 0 = table/view fast load is 3; we default to 0 (table).</summary>
        public void ConfigureOleDbDestination(string name, string connectionManager, string table, int accessMode = 0)
        {
            var comp = Require(name);
            var inst = comp.Instantiate();
            WireConnection(comp, connectionManager);
            inst.SetComponentProperty("AccessMode", accessMode);
            inst.SetComponentProperty("OpenRowset", table);
            ReinitUnderConnection(inst);
        }

        // ---------------- transformations ----------------

        /// <summary>Marks upstream columns as readonly inputs so expressions can reference them.</summary>
        public void ExposeAllInputColumns(string name)
        {
            var comp = Require(name);
            var inst = comp.Instantiate();
            var input = comp.InputCollection[0];
            var vInput = input.GetVirtualInput();
            foreach (Wrapper.IDTSVirtualInputColumn100 vcol in vInput.VirtualInputColumnCollection)
                inst.SetUsageType(input.ID, vInput, vcol.LineageID, Wrapper.DTSUsageType.UT_READONLY);
        }

        /// <summary>Derived Column: adds a computed output column with an SSIS expression.</summary>
        public int ConfigureDerivedColumn(string name, string newColumnName, string expression,
            Rt.DataType dataType, int length = 0, int precision = 0, int scale = 0, int codePage = 0)
        {
            var comp = Require(name);
            var inst = comp.Instantiate();
            var output = comp.OutputCollection[0];
            var col = output.OutputColumnCollection.New();
            col.Name = newColumnName;
            col.ErrorRowDisposition = Wrapper.DTSRowDisposition.RD_FailComponent;
            col.TruncationRowDisposition = Wrapper.DTSRowDisposition.RD_FailComponent;
            inst.SetOutputColumnDataTypeProperties(output.ID, col.ID, dataType, length, precision, scale, codePage);
            // Derived Column stores its expression as custom properties on the output column.
            SetCustomProperty(col.CustomPropertyCollection, "Expression", expression);
            SetCustomProperty(col.CustomPropertyCollection, "FriendlyExpression", expression);
            return col.ID;
        }

        /// <summary>Data Conversion: adds a converted output column derived from an input column.</summary>
        public int ConfigureDataConversion(string name, string inputColumnName, string newColumnName,
            Rt.DataType dataType, int length = 0, int precision = 0, int scale = 0, int codePage = 0)
        {
            var comp = Require(name);
            var inst = comp.Instantiate();
            var input = comp.InputCollection[0];
            var vInput = input.GetVirtualInput();
            var lineage = FindVirtualColumnLineage(vInput, inputColumnName);
            inst.SetUsageType(input.ID, vInput, lineage, Wrapper.DTSUsageType.UT_READONLY);

            var output = comp.OutputCollection[0];
            var col = output.OutputColumnCollection.New();
            col.Name = newColumnName;
            col.ErrorRowDisposition = Wrapper.DTSRowDisposition.RD_FailComponent;
            col.TruncationRowDisposition = Wrapper.DTSRowDisposition.RD_FailComponent;
            inst.SetOutputColumnDataTypeProperties(output.ID, col.ID, dataType, length, precision, scale, codePage);
            SetCustomProperty(col.CustomPropertyCollection, "SourceInputColumnLineageID", lineage);
            SetCustomProperty(col.CustomPropertyCollection, "FastParse", false);
            return col.ID;
        }

        /// <summary>Conditional Split: adds an output with a boolean expression and evaluation order.</summary>
        public int AddConditionalSplitCase(string name, string outputName, string expression, int evaluationOrder)
        {
            var comp = Require(name);
            var inst = comp.Instantiate();
            var output = comp.OutputCollection.New();
            output.Name = outputName;
            output.ExclusionGroup = 1;
            output.SynchronousInputID = comp.InputCollection[0].ID;
            output.ErrorRowDisposition = Wrapper.DTSRowDisposition.RD_FailComponent;
            output.TruncationRowDisposition = Wrapper.DTSRowDisposition.RD_FailComponent;
            SetCustomProperty(output.CustomPropertyCollection, "Expression", expression);
            SetCustomProperty(output.CustomPropertyCollection, "FriendlyExpression", expression);
            SetCustomProperty(output.CustomPropertyCollection, "EvaluationOrder", evaluationOrder);
            return output.ID;
        }

        /// <summary>Lookup: configure reference connection + query, join columns and returned columns.</summary>
        public void ConfigureLookup(string name, string connectionManager, string referenceSql,
            IEnumerable<(string inputColumn, string referenceColumn)> joins,
            IEnumerable<string> returnColumns, int noMatchBehavior = 1)
        {
            var comp = Require(name);
            var inst = comp.Instantiate();
            WireConnection(comp, connectionManager);
            inst.SetComponentProperty("SqlCommand", referenceSql);
            inst.SetComponentProperty("NoMatchBehavior", noMatchBehavior); // 1 = send to no-match output
            inst.AcquireConnections(null);
            inst.ReinitializeMetaData();

            var input = comp.InputCollection[0];
            var vInput = input.GetVirtualInput();
            foreach (var (inputColumn, referenceColumn) in joins)
            {
                var lineage = FindVirtualColumnLineage(vInput, inputColumn);
                inst.SetUsageType(input.ID, vInput, lineage, Wrapper.DTSUsageType.UT_READONLY);
                var inputCol = FindInputColumnByLineage(input, lineage);
                inst.SetInputColumnProperty(input.ID, inputCol.ID, "JoinToReferenceColumn", referenceColumn);
            }
            var matchOutput = comp.OutputCollection[0];
            foreach (var refCol in returnColumns)
            {
                var col = matchOutput.OutputColumnCollection.New();
                col.Name = refCol;
                inst.SetOutputColumnProperty(matchOutput.ID, col.ID, "CopyFromReferenceColumn", refCol);
            }
            inst.ReleaseConnections();
        }

        // ---------------- inspection ----------------

        /// <summary>Structured snapshot of a single component (inputs/outputs/columns/lineage).</summary>
        public ComponentInfo InspectComponent(string name)
        {
            var comp = Require(name);
            var info = new ComponentInfo { Name = comp.Name, Id = comp.ID, ComponentClassId = comp.ComponentClassID };
            foreach (Wrapper.IDTSRuntimeConnection100 rc in comp.RuntimeConnectionCollection)
                if (!string.IsNullOrEmpty(rc.ConnectionManagerID))
                    info.ConnectionManagers.Add(rc.ConnectionManagerID);
            foreach (Wrapper.IDTSInput100 inp in comp.InputCollection)
            {
                var io = new InputOutputInfo { Name = inp.Name, Id = inp.ID };
                foreach (Wrapper.IDTSInputColumn100 c in inp.InputColumnCollection)
                    io.Columns.Add(new ColumnInfo { LineageId = c.LineageID, DataType = c.DataType.ToString() });
                info.Inputs.Add(io);
            }
            foreach (Wrapper.IDTSOutput100 outp in comp.OutputCollection)
            {
                var io = new InputOutputInfo { Name = outp.Name, Id = outp.ID, IsErrorOut = outp.IsErrorOut };
                foreach (Wrapper.IDTSOutputColumn100 c in outp.OutputColumnCollection)
                    io.Columns.Add(new ColumnInfo { Name = c.Name, LineageId = c.LineageID, DataType = c.DataType.ToString() });
                info.Outputs.Add(io);
            }
            return info;
        }

        // expose internals for the mapping engine
        internal Wrapper.MainPipe Pipe => _pipe;
        internal Wrapper.IDTSComponentMetaData100 Require(string name) =>
            FindOrNull(name) ?? throw new BuilderException(BuilderErrorCode.TaskNotFound, $"component '{name}' not found");

        // ---------------- helpers ----------------

        private Wrapper.IDTSComponentMetaData100? FindOrNull(string name)
        {
            foreach (Wrapper.IDTSComponentMetaData100 c in _pipe.ComponentMetaDataCollection)
                if (c.Name == name) return c;
            return null;
        }

        private void WireConnection(Wrapper.IDTSComponentMetaData100 comp, string connectionManagerName)
        {
            Dts.ConnectionManager? cm = null;
            foreach (Dts.ConnectionManager c in _pkg.Connections)
                if (string.Equals(c.Name, connectionManagerName, StringComparison.OrdinalIgnoreCase)) { cm = c; break; }
            if (cm == null)
                throw new BuilderException(BuilderErrorCode.InvalidPrecedence, $"connection manager '{connectionManagerName}' not found");
            if (comp.RuntimeConnectionCollection.Count == 0)
                throw new BuilderException(BuilderErrorCode.MutationError, "component has no runtime connection slot");
            comp.RuntimeConnectionCollection[0].ConnectionManagerID = cm.ID;
            comp.RuntimeConnectionCollection[0].ConnectionManager = Dts.DtsConvert.GetExtendedInterface(cm);
        }

        private static void SetCustomProperty(Wrapper.IDTSCustomPropertyCollection100 props, string name, object value)
        {
            foreach (Wrapper.IDTSCustomProperty100 p in props)
                if (p.Name == name) { p.Value = value; return; }
            var np = props.New();
            np.Name = name;
            np.Value = value;
        }

        private static void ReinitUnderConnection(Wrapper.IDTSDesigntimeComponent100 inst)
        {
            inst.AcquireConnections(null);
            inst.ReinitializeMetaData();
            inst.ReleaseConnections();
        }

        private Wrapper.IDTSOutput100 ResolveOutput(Wrapper.IDTSComponentMetaData100 comp, string? outputName, bool useErrorOutput)
        {
            if (!string.IsNullOrEmpty(outputName))
            {
                foreach (Wrapper.IDTSOutput100 o in comp.OutputCollection)
                    if (o.Name == outputName) return o;
                throw new BuilderException(BuilderErrorCode.InvalidPrecedence, $"output '{outputName}' not found on '{comp.Name}'");
            }
            foreach (Wrapper.IDTSOutput100 o in comp.OutputCollection)
                if (o.IsErrorOut == useErrorOutput) return o;
            throw new BuilderException(BuilderErrorCode.InvalidPrecedence,
                $"no {(useErrorOutput ? "error" : "regular")} output on '{comp.Name}'");
        }

        private Wrapper.IDTSInput100 ResolveInput(Wrapper.IDTSComponentMetaData100 comp, string? inputName)
        {
            if (!string.IsNullOrEmpty(inputName))
            {
                foreach (Wrapper.IDTSInput100 i in comp.InputCollection)
                    if (i.Name == inputName) return i;
                throw new BuilderException(BuilderErrorCode.InvalidPrecedence, $"input '{inputName}' not found on '{comp.Name}'");
            }
            if (comp.InputCollection.Count == 0)
                throw new BuilderException(BuilderErrorCode.InvalidPrecedence, $"'{comp.Name}' has no input");
            return comp.InputCollection[0];
        }

        private static int FindVirtualColumnLineage(Wrapper.IDTSVirtualInput100 vInput, string columnName)
        {
            foreach (Wrapper.IDTSVirtualInputColumn100 v in vInput.VirtualInputColumnCollection)
                if (string.Equals(v.Name, columnName, StringComparison.OrdinalIgnoreCase)) return v.LineageID;
            throw new BuilderException(BuilderErrorCode.MissingSource, $"upstream column '{columnName}' not found");
        }

        private static Wrapper.IDTSInputColumn100 FindInputColumnByLineage(Wrapper.IDTSInput100 input, int lineage)
        {
            foreach (Wrapper.IDTSInputColumn100 c in input.InputColumnCollection)
                if (c.LineageID == lineage) return c;
            throw new BuilderException(BuilderErrorCode.InvalidLineageState, $"input column with lineage {lineage} not found");
        }

        private bool BelongsTo(Wrapper.IDTSPath100 p, Wrapper.IDTSComponentMetaData100 comp)
        {
            var startComp = p.StartPoint != null ? ComponentOf(p.StartPoint.ID) : -1;
            var endComp = p.EndPoint != null ? ComponentOfInput(p.EndPoint.ID) : -1;
            return startComp == comp.ID || endComp == comp.ID;
        }

        private int ComponentOf(int outputId)
        {
            foreach (Wrapper.IDTSComponentMetaData100 c in _pipe.ComponentMetaDataCollection)
                foreach (Wrapper.IDTSOutput100 o in c.OutputCollection)
                    if (o.ID == outputId) return c.ID;
            return -1;
        }

        private int ComponentOfInput(int inputId)
        {
            foreach (Wrapper.IDTSComponentMetaData100 c in _pipe.ComponentMetaDataCollection)
                foreach (Wrapper.IDTSInput100 i in c.InputCollection)
                    if (i.ID == inputId) return c.ID;
            return -1;
        }
    }
}
