using System;
using System.Data.SqlClient;

namespace SsisMcp.Ssis.Execution
{
    /// <summary>
    /// Verifies destination data after a package runs (row counts, scalar values, expected results).
    /// A package that exits 0 is not proof of correctness — this checks the data actually landed.
    /// Backs the execution.verify family; used once execution is unblocked (signed host present).
    /// </summary>
    public sealed class DestinationDataVerifier
    {
        private readonly string _connectionString;

        public DestinationDataVerifier(string connectionString) => _connectionString = connectionString;

        public static DestinationDataVerifier LocalSql(string catalog = "tempdb") =>
            new DestinationDataVerifier($"Data Source=.;Initial Catalog={catalog};Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5");

        public int RowCount(string table) => Convert.ToInt32(Scalar($"SELECT COUNT(*) FROM {table}"));

        public object? Scalar(string sql)
        {
            using (var c = new SqlConnection(_connectionString))
            {
                c.Open();
                using (var cmd = new SqlCommand(sql, c)) return cmd.ExecuteScalar();
            }
        }

        /// <summary>True when the scalar query returns exactly the expected value.</summary>
        public bool AssertScalar(string sql, object expected)
        {
            var actual = Scalar(sql);
            if (actual == null || actual == DBNull.Value) return expected == null;
            return actual.ToString() == expected.ToString();
        }
    }
}
