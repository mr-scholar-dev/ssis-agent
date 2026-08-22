using System;
using System.IO;
using SsisMcp.Core.Packages;
using Dts = Microsoft.SqlServer.Dts.Runtime;

namespace SsisMcp.Ssis
{
    /// <summary>
    /// Thin, API-first wrapper over the SSIS Object Model for the Phase 1 proof of concept:
    /// create/load/save/inspect/validate a package via <see cref="Dts.Application"/> — no XML
    /// string surgery, no UI automation.
    /// </summary>
    public sealed class PackageService
    {
        private readonly Dts.Application _app = new Dts.Application();

        /// <summary>Loads a .dtsx from disk into an in-memory package.</summary>
        public Dts.Package Load(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Package not found", path);
            return _app.LoadPackage(path, null);
        }

        /// <summary>Saves a package to a .dtsx file.</summary>
        public void Save(Dts.Package package, string path)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _app.SaveToXml(path, package, null);
        }

        /// <summary>
        /// Builds a minimal but real package: one Sequence container holding one Execute SQL Task,
        /// plus an OLE DB connection manager, at the requested target server version.
        /// </summary>
        public Dts.Package CreateMinimalPackage(string name, string targetServerVersion)
        {
            var pkg = new Dts.Package { Name = name };

            // Version-adapter seed. The runtime Package does not expose a strongly-typed
            // TargetServerVersion property (it is a project/design-time concept), so we set it
            // defensively through the Properties bag and record whether the runtime accepted it.
            TrySetTargetServerVersion(pkg, targetServerVersion);

            var conn = pkg.Connections.Add("OLEDB");
            conn.Name = "PracticaOrigen";
            conn.ConnectionString =
                "Data Source=.;Initial Catalog=tempdb;Provider=MSOLEDBSQL;Integrated Security=SSPI;";

            var seqContainer = (Dts.Sequence)pkg.Executables.Add("STOCK:SEQUENCE");
            seqContainer.Name = "SEQ_Main";

            var sqlHost = (Dts.TaskHost)seqContainer.Executables.Add("Microsoft.ExecuteSQLTask");
            sqlHost.Name = "SqlBorrar";
            sqlHost.Properties["Connection"].SetValue(sqlHost, conn.Name);
            sqlHost.Properties["SqlStatementSource"].SetValue(sqlHost, "SELECT 1;");

            return pkg;
        }

        /// <summary>Produces a structured snapshot for MCP consumers.</summary>
        public PackageInfo Inspect(Dts.Package package)
        {
            var info = new PackageInfo
            {
                Name = package.Name,
                TargetServerVersion = TryGetTargetServerVersion(package),
                ProtectionLevel = package.ProtectionLevel.ToString()
            };

            foreach (Dts.Executable exec in package.Executables)
                AddExecutable(info, exec);

            foreach (Dts.ConnectionManager cm in package.Connections)
            {
                info.Connections.Add(new ConnectionInfo
                {
                    Name = cm.Name,
                    CreationName = cm.CreationName,
                    Id = cm.ID
                });
            }
            return info;
        }

        private static void AddExecutable(PackageInfo info, Dts.Executable exec)
        {
            if (exec is Dts.TaskHost th)
            {
                info.Executables.Add(new ExecutableInfo
                {
                    Name = th.Name,
                    TypeName = th.InnerObject?.GetType().Name,
                    CreationName = th.CreationName
                });
            }
            else if (exec is Dts.Sequence seq)
            {
                info.Executables.Add(new ExecutableInfo
                {
                    Name = seq.Name,
                    TypeName = "Sequence",
                    CreationName = seq.CreationName
                });
                foreach (Dts.Executable child in seq.Executables)
                    AddExecutable(info, child);
            }
            else if (exec is Dts.DtsContainer c)
            {
                info.Executables.Add(new ExecutableInfo { Name = c.Name, TypeName = c.GetType().Name });
            }
        }

        /// <summary>Runs SSIS validation and returns the raw result.</summary>
        public Dts.DTSExecResult Validate(Dts.Package package)
        {
            return package.Validate(package.Connections, package.Variables, null, null);
        }

        /// <summary>
        /// Best-effort set of TargetServerVersion via the Properties bag. Returns true if the
        /// runtime exposed and accepted the property. Empirically records what this runtime supports.
        /// </summary>
        public bool TrySetTargetServerVersion(Dts.Package package, string year)
        {
            try
            {
                var prop = package.Properties["TargetServerVersion"];
                if (prop == null) return false;
                prop.SetValue(package, ParseTargetServerVersion(year));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Reads TargetServerVersion defensively; null if the runtime does not expose it.</summary>
        public string? TryGetTargetServerVersion(Dts.Package package)
        {
            try
            {
                var prop = package.Properties["TargetServerVersion"];
                return prop?.GetValue(package)?.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static object ParseTargetServerVersion(string year)
        {
            switch (year.Trim())
            {
                case "2016": return Dts.DTSTargetServerVersion.SQLServer2016;
                case "2017": return Dts.DTSTargetServerVersion.SQLServer2017;
                case "2019": return Dts.DTSTargetServerVersion.SQLServer2019;
                case "2022": return Dts.DTSTargetServerVersion.SQLServer2022;
                default:
                    throw new ArgumentOutOfRangeException(nameof(year),
                        $"Unsupported/unknown TargetServerVersion '{year}' for this runtime.");
            }
        }
    }
}
