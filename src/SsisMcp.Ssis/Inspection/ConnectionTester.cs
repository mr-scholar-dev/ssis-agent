using System;
using Dts = Microsoft.SqlServer.Dts.Runtime;

namespace SsisMcp.Ssis.Inspection
{
    /// <summary>Result of testing a connection manager (acquire without executing the package).</summary>
    public sealed class ConnectionTestResult
    {
        public string Name { get; set; } = "";
        public bool Ok { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Tests that a named connection manager can actually connect — it AcquireConnection()s and
    /// releases, without running the package. Provider/credential/reachability problems surface as a
    /// structured failure instead of only appearing at execution time.
    /// </summary>
    public sealed class ConnectionTester
    {
        private readonly PackageService _svc;
        public ConnectionTester(PackageService? svc = null) => _svc = svc ?? new PackageService();

        public ConnectionTestResult Test(string packagePath, string connectionName)
        {
            var result = new ConnectionTestResult { Name = connectionName };
            var pkg = _svc.Load(packagePath);
            Dts.ConnectionManager? cm = null;
            foreach (Dts.ConnectionManager c in pkg.Connections)
                if (string.Equals(c.Name, connectionName, StringComparison.OrdinalIgnoreCase)) { cm = c; break; }
            if (cm == null) { result.Error = $"connection manager '{connectionName}' not found"; return result; }

            object? handle = null;
            try
            {
                handle = cm.AcquireConnection(null);
                result.Ok = handle != null;
                if (!result.Ok) result.Error = "AcquireConnection returned null";
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Error = ex.GetType().Name + ": " + (ex.InnerException?.Message ?? ex.Message);
            }
            finally
            {
                try { if (handle != null) cm.ReleaseConnection(handle); } catch { /* best effort */ }
            }
            return result;
        }
    }
}
