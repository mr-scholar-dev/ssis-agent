using System;
using System.Collections.Generic;
using Dts = Microsoft.SqlServer.Dts.Runtime;
using Rt = Microsoft.SqlServer.Dts.Runtime.Wrapper;

namespace SsisMcp.Ssis.Building
{
    /// <summary>
    /// Creates connection managers for the data sources the benchmark needs. On this x64 host the
    /// built-in EXCEL connection manager defaults to Jet.OLEDB.4.0 (32-bit only, not registered),
    /// so we override its ConnectionString to ACE. Access/Excel both go through ACE OLE DB.
    /// </summary>
    public static class ConnectionFactory
    {
        /// <summary>Excel connection manager backed by ACE (works on x64). xlsx = "Excel 12.0 Xml", xls = "Excel 8.0".</summary>
        public static Dts.ConnectionManager AddExcel(Dts.Package pkg, string name, string filePath, bool xlsx = true, bool hdr = true)
        {
            var cm = pkg.Connections.Add("EXCEL");
            cm.Name = name;
            var ext = xlsx ? "Excel 12.0 Xml" : "Excel 8.0";
            cm.ConnectionString =
                $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={filePath};Extended Properties=\"{ext};HDR={(hdr ? "YES" : "NO")}\";";
            return cm;
        }

        /// <summary>ACE OLE DB connection manager for Access (.accdb/.mdb) read/write via OLE DB Source/Destination.</summary>
        public static Dts.ConnectionManager AddAccess(Dts.Package pkg, string name, string filePath)
        {
            var cm = pkg.Connections.Add("OLEDB");
            cm.Name = name;
            cm.ConnectionString = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={filePath};Persist Security Info=False;";
            return cm;
        }

        /// <summary>
        /// Delimited Flat File connection manager with an explicit column list (SSIS does not infer
        /// columns from the file — the wizard's "suggest types" must be reproduced here). The last
        /// column carries the row delimiter; the rest carry the column delimiter.
        /// </summary>
        public static Dts.ConnectionManager AddFlatFile(Dts.Package pkg, string name, string filePath,
            IReadOnlyList<(string name, Rt.DataType dataType, int width)> columns,
            string columnDelimiter = ",", string rowDelimiter = "\r\n", bool headerRow = true)
        {
            var cm = pkg.Connections.Add("FLATFILE");
            cm.Name = name;
            cm.ConnectionString = filePath;
            cm.Properties["Format"].SetValue(cm, "Delimited");
            cm.Properties["ColumnNamesInFirstDataRow"].SetValue(cm, headerRow);

            var ff = (Rt.IDTSConnectionManagerFlatFile100)cm.InnerObject;
            ff.RowDelimiter = rowDelimiter;
            ff.HeaderRowDelimiter = rowDelimiter;
            for (var i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                var c = ff.Columns.Add();
                c.ColumnType = "Delimited";
                c.ColumnDelimiter = i == columns.Count - 1 ? rowDelimiter : columnDelimiter;
                c.DataType = col.dataType;
                if (col.width > 0) c.MaximumWidth = col.width;
                ((Rt.IDTSName100)c).Name = col.name;
            }
            return cm;
        }

        /// <summary>SSIS moniker for the ADO.NET (System.Data.SqlClient) connection manager. Centralized.</summary>
        public const string AdoNetSqlConnectionMoniker =
            "ADO.NET:System.Data.SqlClient.SqlConnection, System.Data, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

        /// <summary>ADO.NET (SqlClient) connection manager for a SQL Server database.</summary>
        public static Dts.ConnectionManager AddAdoNetSql(Dts.Package pkg, string name, string dataSource, string catalog)
        {
            var cm = pkg.Connections.Add(AdoNetSqlConnectionMoniker);
            cm.Name = name;
            cm.ConnectionString = $"Data Source={dataSource};Initial Catalog={catalog};Integrated Security=True;";
            return cm;
        }

        /// <summary>SQL Server OLE DB connection manager (MSOLEDBSQL).</summary>
        public static Dts.ConnectionManager AddSqlOleDb(Dts.Package pkg, string name, string dataSource, string catalog)
        {
            var cm = pkg.Connections.Add("OLEDB");
            cm.Name = name;
            cm.ConnectionString = $"Data Source={dataSource};Initial Catalog={catalog};Provider=MSOLEDBSQL;Integrated Security=SSPI;";
            return cm;
        }
    }
}
