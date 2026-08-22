using System;
using Dts = Microsoft.SqlServer.Dts.Runtime;
using Wrapper = Microsoft.SqlServer.Dts.Pipeline.Wrapper;

namespace SsisMcp.IntegrationTests.Support
{
    /// <summary>
    /// Builds REAL SSIS fixtures via the Object Model + Pipeline API for inspector tests.
    /// This is test scaffolding, not the public Data Flow builder (that is a later phase).
    /// </summary>
    internal static class FixtureBuilder
    {
        /// <summary>Empty package with a single OLE DB connection manager, for builder tests.</summary>
        public static Dts.Package BuildEmptyWithConnection(string packageName = "BuildTarget", string connectionName = "Origen")
        {
            var pkg = new Dts.Package { Name = packageName };
            var cm = pkg.Connections.Add("OLEDB");
            cm.Name = connectionName;
            cm.ConnectionString = "Data Source=.;Initial Catalog=master;Provider=MSOLEDBSQL;Integrated Security=SSPI;";
            return pkg;
        }

        /// <summary>
        /// Control Flow fixture: two Execute SQL Tasks with a Completion precedence constraint
        /// (Task1 -> Task2) inside the package, plus an OLE DB connection manager.
        /// </summary>
        public static Dts.Package BuildControlFlowWithPrecedence()
        {
            var pkg = new Dts.Package { Name = "ControlFlowFixture" };
            var cm = pkg.Connections.Add("OLEDB");
            cm.Name = "Origen";
            cm.ConnectionString = "Data Source=.;Initial Catalog=master;Provider=MSOLEDBSQL;Integrated Security=SSPI;";

            var t1 = (Dts.TaskHost)pkg.Executables.Add("Microsoft.ExecuteSQLTask");
            t1.Name = "SqlBorrar";
            t1.Properties["Connection"].SetValue(t1, "Origen");
            t1.Properties["SqlStatementSource"].SetValue(t1, "SELECT 1;");

            var t2 = (Dts.TaskHost)pkg.Executables.Add("Microsoft.ExecuteSQLTask");
            t2.Name = "SqlCargar";
            t2.Properties["Connection"].SetValue(t2, "Origen");
            t2.Properties["SqlStatementSource"].SetValue(t2, "SELECT 2;");

            var pc = pkg.PrecedenceConstraints.Add(t1, t2);
            pc.Value = Dts.DTSExecResult.Completion;
            pc.EvalOp = Dts.DTSPrecedenceEvalOp.Constraint;

            return pkg;
        }

        /// <summary>
        /// Data Flow fixture: OLE DB Source (SELECT from sys.objects on the local instance) -> Derived
        /// Column, connected by a path. Exercises components, inputs/outputs, columns, lineage ids,
        /// external metadata and per-component connection managers. Requires local SQL connectivity.
        /// </summary>
        public static Dts.Package BuildDataFlowWithOleDbSource(string dataSource = ".", string catalog = "master")
        {
            var pkg = new Dts.Package { Name = "DataFlowFixture" };
            var cm = pkg.Connections.Add("OLEDB");
            cm.Name = "SrcDb";
            cm.ConnectionString =
                $"Data Source={dataSource};Initial Catalog={catalog};Provider=MSOLEDBSQL;Integrated Security=SSPI;";

            var dft = (Dts.TaskHost)pkg.Executables.Add("Microsoft.Pipeline");
            dft.Name = "DFT_Load";
            var pipe = (Wrapper.MainPipe)dft.InnerObject;

            // --- OLE DB Source ---
            var src = pipe.ComponentMetaDataCollection.New();
            src.ComponentClassID = "Microsoft.OLEDBSource";
            var srcInst = src.Instantiate();
            srcInst.ProvideComponentProperties();
            src.Name = "OLEDB_Src"; // set after ProvideComponentProperties (which resets Name)
            src.RuntimeConnectionCollection[0].ConnectionManagerID = cm.ID;
            src.RuntimeConnectionCollection[0].ConnectionManager =
                Dts.DtsConvert.GetExtendedInterface(cm);
            srcInst.SetComponentProperty("AccessMode", 2); // SQL command
            srcInst.SetComponentProperty("SqlCommand", "SELECT name, object_id FROM sys.objects");
            srcInst.AcquireConnections(null);
            srcInst.ReinitializeMetaData();
            srcInst.ReleaseConnections();

            // --- Derived Column ---
            var der = pipe.ComponentMetaDataCollection.New();
            der.ComponentClassID = "Microsoft.DerivedColumn";
            var derInst = der.Instantiate();
            derInst.ProvideComponentProperties();
            der.Name = "Derived";

            var path = pipe.PathCollection.New();
            path.AttachPathAndPropagateNotifications(src.OutputCollection[0], der.InputCollection[0]);

            var input = der.InputCollection[0];
            var vInput = input.GetVirtualInput();
            foreach (Wrapper.IDTSVirtualInputColumn100 vcol in vInput.VirtualInputColumnCollection)
                derInst.SetUsageType(input.ID, vInput, vcol.LineageID, Wrapper.DTSUsageType.UT_READONLY);
            derInst.ReinitializeMetaData();

            return pkg;
        }
    }
}
