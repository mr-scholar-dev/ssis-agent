using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SsisMcp.Ssis.Inspection
{
    // ---- DTOs (read-only source analysis; consumed by the planner over MCP) ----
    public sealed class ColumnSchema { public string Name { get; set; } = ""; public string DataType { get; set; } = ""; public bool Nullable { get; set; } = true; public int Length { get; set; } }
    public sealed class TableSchema { public string Name { get; set; } = ""; public List<ColumnSchema> Columns { get; } = new List<ColumnSchema>(); public int InsertRowCount { get; set; } public bool HasInserts => InsertRowCount > 0; }
    public sealed class SqlScriptInfo { public string? DatabaseName { get; set; } public List<TableSchema> Tables { get; } = new List<TableSchema>(); public List<string> ForeignKeys { get; } = new List<string>(); }
    public sealed class SheetInfo { public string Name { get; set; } = ""; public bool HeaderGuess { get; set; } public List<ColumnSchema> Columns { get; } = new List<ColumnSchema>(); public int RowCount { get; set; } }
    public sealed class WorkbookInfo { public List<SheetInfo> Sheets { get; } = new List<SheetInfo>(); }
    public sealed class DiscoveredFile { public string Path { get; set; } = ""; public string Extension { get; set; } = ""; public string Kind { get; set; } = ""; public long SizeBytes { get; set; } }

    /// <summary>Classifies files in a directory by extension so the planner can find its inputs.</summary>
    public static class FileDiscoverer
    {
        public static List<DiscoveredFile> Discover(string dir, bool recursive = false)
        {
            if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(dir);
            var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var list = new List<DiscoveredFile>();
            foreach (var f in Directory.GetFiles(dir, "*.*", opt))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                list.Add(new DiscoveredFile
                {
                    Path = f,
                    Extension = ext,
                    SizeBytes = new FileInfo(f).Length,
                    Kind = ext switch
                    {
                        ".sql" => "sql",
                        ".xls" or ".xlsx" or ".xlsm" => "excel",
                        ".accdb" or ".mdb" => "access",
                        ".dtsx" => "dtsx",
                        ".dtproj" => "dtproj",
                        ".docx" or ".doc" or ".pdf" or ".txt" or ".md" => "doc",
                        _ => "other"
                    }
                });
            }
            return list;
        }
    }

    /// <summary>
    /// Best-effort parser for a T-SQL script: CREATE DATABASE / CREATE TABLE (columns + nullability) +
    /// a count of INSERT statements per table + FK declarations. Regex-based and reported as such — the
    /// planner treats results as evidence, not ground truth, and asks when ambiguous.
    /// </summary>
    public static class SqlScriptInspector
    {
        public static SqlScriptInfo Inspect(string path)
        {
            var text = File.ReadAllText(path);
            var info = new SqlScriptInfo();
            var db = Regex.Match(text, @"CREATE\s+DATABASE\s+\[?(?<n>[A-Za-z0-9_]+)\]?", RegexOptions.IgnoreCase);
            if (db.Success) info.DatabaseName = db.Groups["n"].Value;

            foreach (Match m in Regex.Matches(text, @"CREATE\s+TABLE\s+\[?(?<name>[A-Za-z0-9_]+)\]?\s*\((?<body>.*?)\)\s*;", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var t = new TableSchema { Name = m.Groups["name"].Value };
                foreach (var rawLine in SplitColumns(m.Groups["body"].Value))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0) continue;
                    // skip table-level constraints
                    if (Regex.IsMatch(line, @"^(constraint|primary\s+key|foreign\s+key|unique|check)\b", RegexOptions.IgnoreCase)) continue;
                    var cm = Regex.Match(line, @"^\[?(?<col>[A-Za-z0-9_]+)\]?\s+(?<type>[A-Za-z0-9_]+(\s*\(\s*\d+(\s*,\s*\d+)?\s*\))?)(?<rest>.*)$", RegexOptions.IgnoreCase);
                    if (!cm.Success) continue;
                    var col = new ColumnSchema
                    {
                        Name = cm.Groups["col"].Value,
                        DataType = Regex.Replace(cm.Groups["type"].Value, @"\s+", ""),
                        Nullable = !Regex.IsMatch(cm.Groups["rest"].Value, @"not\s+null", RegexOptions.IgnoreCase)
                    };
                    var len = Regex.Match(col.DataType, @"\((?<l>\d+)");
                    if (len.Success) col.Length = int.Parse(len.Groups["l"].Value);
                    t.Columns.Add(col);
                }
                t.InsertRowCount = Regex.Matches(text, @"insert\s+into\s+\[?" + Regex.Escape(t.Name) + @"\b", RegexOptions.IgnoreCase).Count
                                 + Regex.Matches(text, @"insert\s+\[?" + Regex.Escape(t.Name) + @"\b", RegexOptions.IgnoreCase).Count;
                info.Tables.Add(t);
            }
            foreach (Match m in Regex.Matches(text, @"foreign\s+key\s*\((?<c>[^)]+)\)\s*references\s+\[?(?<ref>[A-Za-z0-9_]+)\]?", RegexOptions.IgnoreCase))
                info.ForeignKeys.Add(m.Groups["c"].Value.Trim() + " -> " + m.Groups["ref"].Value);
            return info;
        }

        // split a CREATE TABLE body on top-level commas (ignore commas inside type parens)
        private static IEnumerable<string> SplitColumns(string body)
        {
            var depth = 0; var start = 0;
            for (var i = 0; i < body.Length; i++)
            {
                if (body[i] == '(') depth++;
                else if (body[i] == ')') depth--;
                else if (body[i] == ',' && depth == 0) { yield return body.Substring(start, i - start); start = i + 1; }
            }
            if (start < body.Length) yield return body.Substring(start);
        }
    }

    /// <summary>Excel workbook schema via ACE OLE DB (x64). Reports sheets, columns and row counts.</summary>
    public static class ExcelInspector
    {
        public static WorkbookInfo Inspect(string path, bool? xlsx = null, bool header = true)
        {
            var isXlsx = xlsx ?? (Path.GetExtension(path).ToLowerInvariant() != ".xls");
            var ext = isXlsx ? "Excel 12.0 Xml" : "Excel 8.0";
            // No IMEX=1: report the natural inferred types (numeric -> Double) so analysis matches what
            // the runtime Excel Source (ConnectionFactory.AddExcel, also no IMEX) actually produces.
            var cs = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={path};Extended Properties=\"{ext};HDR={(header ? "YES" : "NO")}\";";
            var wb = new WorkbookInfo();
            using (var c = new OleDbConnection(cs))
            {
                c.Open();
                var schema = c.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, null);
                if (schema == null) return wb;
                foreach (System.Data.DataRow r in schema.Rows)
                {
                    var sheet = (string)r["TABLE_NAME"];
                    if (!sheet.EndsWith("$") && !sheet.EndsWith("$'")) continue; // worksheets end with $
                    var si = new SheetInfo { Name = sheet.Trim('\''), HeaderGuess = header };
                    // A live reader (NOT SchemaOnly) lets ACE sample rows and infer real column types
                    // (numeric -> Double), matching what the runtime Excel Source produces.
                    using (var cmd = new OleDbCommand($"SELECT * FROM [{sheet}]", c))
                    using (var rdr = cmd.ExecuteReader())
                    {
                        var st = rdr.GetSchemaTable();
                        if (st != null)
                            foreach (System.Data.DataRow cr in st.Rows)
                                si.Columns.Add(new ColumnSchema { Name = Convert.ToString(cr["ColumnName"]) ?? "", DataType = ((Type)cr["DataType"]).Name });
                    }
                    using (var cmd = new OleDbCommand($"SELECT COUNT(*) FROM [{sheet}]", c))
                        si.RowCount = Convert.ToInt32(cmd.ExecuteScalar());
                    wb.Sheets.Add(si);
                }
            }
            return wb;
        }
    }

    /// <summary>Access (.accdb/.mdb) schema via ACE OLE DB. Reports user tables, columns and row counts.</summary>
    public static class AccessInspector
    {
        public static List<TableSchema> Inspect(string path)
        {
            var cs = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={path};Persist Security Info=False;";
            var tables = new List<TableSchema>();
            using (var c = new OleDbConnection(cs))
            {
                c.Open();
                var schema = c.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, new object[] { null!, null!, null!, "TABLE" });
                if (schema == null) return tables;
                foreach (System.Data.DataRow r in schema.Rows)
                {
                    var name = (string)r["TABLE_NAME"];
                    var t = new TableSchema { Name = name };
                    using (var cmd = new OleDbCommand($"SELECT * FROM [{name}]", c))
                    using (var rdr = cmd.ExecuteReader(System.Data.CommandBehavior.SchemaOnly))
                    {
                        var st = rdr.GetSchemaTable();
                        if (st != null)
                            foreach (System.Data.DataRow cr in st.Rows)
                                t.Columns.Add(new ColumnSchema
                                {
                                    Name = Convert.ToString(cr["ColumnName"]) ?? "",
                                    DataType = ((Type)cr["DataType"]).Name,
                                    Nullable = cr["AllowDBNull"] != DBNull.Value && (bool)cr["AllowDBNull"]
                                });
                    }
                    using (var cmd = new OleDbCommand($"SELECT COUNT(*) FROM [{name}]", c))
                        t.InsertRowCount = Convert.ToInt32(cmd.ExecuteScalar());
                    tables.Add(t);
                }
            }
            return tables;
        }
    }
}
