using System;
using System.IO;
using System.Linq;
using SsisMcp.Core.Building;
using SsisMcp.Ssis;
using SsisMcp.Ssis.Building;
using Xunit;
using Dts = Microsoft.SqlServer.Dts.Runtime;
using Wrapper = Microsoft.SqlServer.Dts.Pipeline.Wrapper;
using Rt = Microsoft.SqlServer.Dts.Runtime.Wrapper;

namespace SsisMcp.IntegrationTests
{
    /// <summary>
    /// Flat File Source/Destination (delimited, header row). StructurallyVerified only (execution
    /// EnvironmentBlocked). Covers columns/types/metadata/mappings + save/reload/double-reload.
    /// </summary>
    public sealed class FlatFileTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ff-" + Guid.NewGuid().ToString("N"));
        private readonly PackageService _svc = new PackageService();

        public FlatFileTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static (string, Rt.DataType, int)[] Cols => new[]
        {
            ("Codigo", Rt.DataType.DT_I4, 0),
            ("Nombre", Rt.DataType.DT_STR, 50),
        };

        [Fact]
        public void Flat_file_source_reads_columns_and_roundtrips()
        {
            var csv = Path.Combine(_dir, "in.csv");
            File.WriteAllText(csv, "Codigo,Nombre\r\n1,a\r\n2,b\r\n");

            var pkg = new Dts.Package { Name = "DF" };
            ConnectionFactory.AddFlatFile(pkg, "Ff", csv, Cols);
            var dft = (Dts.TaskHost)pkg.Executables.Add("Microsoft.Pipeline"); dft.Name = "DFT";
            var b = new DataFlowBuilder((Wrapper.MainPipe)dft.InnerObject, pkg);
            b.AddComponent(ComponentKinds.FlatFileSource, "FfSrc");
            b.ConfigureFlatFileSource("FfSrc", "Ff");

            var path = Path.Combine(_dir, "ffsrc.dtsx");
            _svc.Save(pkg, path);

            var info = _svc.InspectFile(path);
            var src = info.DataFlows.Single().Components.Single(c => c.Name == "FfSrc");
            Assert.Equal("source", src.Role);
            var cols = src.Outputs.SelectMany(o => o.Columns).ToList();
            Assert.Contains(cols, c => c.Name == "Codigo" && c.DataType == "DT_I4");
            Assert.Contains(cols, c => c.Name == "Nombre" && c.DataType == "DT_STR");

            // double reload
            Assert.Equal(Dts.DTSExecResult.Success, _svc.Validate(_svc.Load(path)));
            Assert.Equal(Dts.DTSExecResult.Success, _svc.Validate(_svc.Load(path)));
        }

        [Fact]
        public void Flat_file_source_to_destination_roundtrips_with_mappings()
        {
            var inCsv = Path.Combine(_dir, "in2.csv");
            File.WriteAllText(inCsv, "Codigo,Nombre\r\n1,a\r\n");
            var outCsv = Path.Combine(_dir, "out2.csv");
            File.WriteAllText(outCsv, "Codigo,Nombre\r\n"); // header defines destination columns

            var pkg = new Dts.Package { Name = "DF" };
            ConnectionFactory.AddFlatFile(pkg, "In", inCsv, Cols);
            ConnectionFactory.AddFlatFile(pkg, "Out", outCsv, Cols);
            var dft = (Dts.TaskHost)pkg.Executables.Add("Microsoft.Pipeline"); dft.Name = "DFT";
            var b = new DataFlowBuilder((Wrapper.MainPipe)dft.InnerObject, pkg);
            b.AddComponent(ComponentKinds.FlatFileSource, "Src");
            b.ConfigureFlatFileSource("Src", "In");
            b.AddComponent(ComponentKinds.FlatFileDestination, "Dst");
            b.Connect("Src", "Dst");
            b.ConfigureFlatFileDestination("Dst", "Out");
            new MappingEngine(b).AutoMap("Dst");

            var path = Path.Combine(_dir, "ff2.dtsx");
            _svc.Save(pkg, path);

            var info = _svc.InspectFile(path);
            var df = info.DataFlows.Single();
            Assert.Contains(df.Paths, p => p.StartComponent == "Src" && p.EndComponent == "Dst");
            Assert.Equal("destination", df.Components.Single(c => c.Name == "Dst").Role);
            Assert.Equal(Dts.DTSExecResult.Success, _svc.Validate(_svc.Load(path)));
        }
    }
}
