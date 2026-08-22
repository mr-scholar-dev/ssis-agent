using System;
using System.IO;
using SsisMcp.Core.Packages;
using SsisMcp.Ssis.Inspection;
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

        /// <summary>Produces a full structured snapshot (Control Flow + Data Flow) for MCP consumers.</summary>
        public PackageInfo Inspect(Dts.Package package)
        {
            var info = new PackageInfo
            {
                Name = package.Name,
                Id = package.ID,
                TargetServerVersion = TryGetTargetServerVersion(package),
                ProtectionLevel = package.ProtectionLevel.ToString(),
                PackageFormatVersion = TryGetPackageFormatVersion(package)
            };

            foreach (Dts.ConnectionManager cm in package.Connections)
            {
                info.Connections.Add(new ConnectionInfo
                {
                    Name = cm.Name,
                    CreationName = cm.CreationName,
                    Id = cm.ID
                });
            }

            var cmById = BuildConnectionNameMap(package);
            ControlFlowInspector.Populate(package, info, cmById);
            DataFlowInspector.Populate(package, info, cmById);
            return info;
        }

        /// <summary>Loads then inspects a package file, also reading PackageFormatVersion from disk.</summary>
        public PackageInfo InspectFile(string path)
        {
            var pkg = Load(path);
            var info = Inspect(pkg);
            if (info.PackageFormatVersion == null)
                info.PackageFormatVersion = PackageFormatVersionReader.FromFile(path);

            // Task-level connection references come from the file (the OM cannot expose them for
            // COM-backed tasks). Merge them into the executable tree by ObjectName.
            var usage = ConnectionUsageXmlReader.FromFile(path, info.Connections);
            MergeConnectionUsage(info.Executables, usage);
            return info;
        }

        private static void MergeConnectionUsage(
            System.Collections.Generic.IEnumerable<ExecutableInfo> executables,
            System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> usage)
        {
            foreach (var e in executables)
            {
                if (usage.TryGetValue(e.Name, out var names))
                    foreach (var n in names)
                        if (!e.ConnectionManagers.Contains(n)) e.ConnectionManagers.Add(n);
                MergeConnectionUsage(e.Children, usage);
            }
        }

        internal static System.Collections.Generic.Dictionary<string, string> BuildConnectionNameMap(Dts.Package package)
        {
            var map = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (Dts.ConnectionManager cm in package.Connections)
            {
                if (!string.IsNullOrEmpty(cm.ID))
                {
                    map[cm.ID] = cm.Name;
                    map[NormalizeId(cm.ID)] = cm.Name; // tolerate brace/case differences
                }
                if (!string.IsNullOrEmpty(cm.Name)) map[cm.Name] = cm.Name;
            }
            return map;
        }

        /// <summary>Resolves a connection reference (id or name) to a friendly CM name, else echoes it.</summary>
        internal static string ResolveConnection(System.Collections.Generic.Dictionary<string, string> cmById, string reference)
        {
            if (cmById.TryGetValue(reference, out var name)) return name;
            if (cmById.TryGetValue(NormalizeId(reference), out var name2)) return name2;
            return reference;
        }

        private static string NormalizeId(string id) => id.Trim('{', '}').ToUpperInvariant();

        private static int? TryGetPackageFormatVersion(Dts.Package package)
        {
            try
            {
                var prop = package.Properties["PackageFormatVersion"];
                var val = prop?.GetValue(package);
                if (val == null) return null;
                return System.Convert.ToInt32(val);
            }
            catch (System.Exception)
            {
                return null;
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
