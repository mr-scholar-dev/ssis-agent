using System;
using System.Text.RegularExpressions;

namespace SsisMcp.Planner
{
    /// <summary>Normalized SSIS destination type resolved from a SQL column type.</summary>
    public sealed class DestType { public string Dt = ""; public int Length; public int Precision; public int Scale; public int CodePage; }

    /// <summary>
    /// Deterministic SSIS type resolution + conversion decisions. This is the evidence-based core:
    /// when the source and destination pipeline types match it maps directly; when a KNOWN safe
    /// conversion exists it inserts a Data Conversion; otherwise it returns null so the planner raises
    /// an ambiguity instead of guessing.
    /// </summary>
    public static class SsisTypes
    {
        /// <summary>Resolve a SQL column type (e.g. "varchar(50)", "int", "money", "decimal(10,2)") to SSIS.</summary>
        public static DestType ResolveDest(string sqlType)
        {
            var s = sqlType.Trim().ToLowerInvariant();
            var baseName = Regex.Match(s, @"^[a-z0-9_]+").Value;
            int p1 = 0, p2 = 0;
            var m = Regex.Match(s, @"\((?<a>\d+)(\s*,\s*(?<b>\d+))?\)");
            if (m.Success) { p1 = int.Parse(m.Groups["a"].Value); if (m.Groups["b"].Success) p2 = int.Parse(m.Groups["b"].Value); }
            switch (baseName)
            {
                case "varchar": case "char": return new DestType { Dt = "DT_STR", Length = p1 > 0 ? p1 : 50, CodePage = 1252 };
                case "nvarchar": case "nchar": return new DestType { Dt = "DT_WSTR", Length = p1 > 0 ? p1 : 50 };
                case "text": return new DestType { Dt = "DT_STR", Length = 4000, CodePage = 1252 };
                case "int": case "integer": return new DestType { Dt = "DT_I4" };
                case "smallint": return new DestType { Dt = "DT_I2" };
                case "bigint": return new DestType { Dt = "DT_I8" };
                case "tinyint": return new DestType { Dt = "DT_UI1" };
                case "bit": return new DestType { Dt = "DT_BOOL" };
                case "date": return new DestType { Dt = "DT_DBDATE" };
                case "datetime": case "datetime2": case "smalldatetime": return new DestType { Dt = "DT_DBTIMESTAMP" };
                case "money": case "smallmoney": return new DestType { Dt = "DT_CY" };
                case "decimal": case "numeric": return new DestType { Dt = "DT_NUMERIC", Precision = p1 > 0 ? p1 : 18, Scale = p2 };
                case "float": case "real": return new DestType { Dt = "DT_R8" };
                default: return new DestType { Dt = "" }; // unknown -> caller raises ambiguity
            }
        }

        /// <summary>Resolve the SSIS type a SOURCE column arrives as, given the source kind.</summary>
        public static string ResolveSource(string sourceKind, string srcType)
        {
            var t = srcType.Trim();
            if (sourceKind == "sql")
            {
                // OLE DB SQL Server source keeps varchar as DT_STR, nvarchar as DT_WSTR.
                var d = ResolveDest(t);
                return d.Dt;
            }
            // excel / access report .NET framework type names via the schema table
            switch (t.ToLowerInvariant())
            {
                case "string": return sourceKind == "excel" ? "DT_WSTR" : "DT_WSTR";
                case "double": return "DT_R8";
                case "single": return "DT_R4";
                case "int16": return "DT_I2";
                case "int32": return "DT_I4";
                case "int64": return "DT_I8";
                case "byte": return "DT_UI1";
                case "boolean": return "DT_BOOL";
                case "datetime": return sourceKind == "excel" ? "DT_DATE" : "DT_DBTIMESTAMP";
                case "decimal": return "DT_NUMERIC";
                default: return "";
            }
        }

        /// <summary>
        /// true = a Data Conversion is required; false = direct map; null = no safe conversion (ambiguous).
        /// </summary>
        public static bool? NeedsConversion(string srcDt, string destDt)
        {
            if (string.IsNullOrEmpty(srcDt) || string.IsNullOrEmpty(destDt)) return null;
            if (srcDt == destDt) return false;
            // date/time families that map directly enough
            if (srcDt == "DT_DBDATE" && destDt == "DT_DBDATE") return false;

            var known = new (string from, string to)[]
            {
                ("DT_WSTR","DT_STR"), ("DT_STR","DT_WSTR"),
                ("DT_R8","DT_I4"), ("DT_R8","DT_I2"), ("DT_R8","DT_I8"), ("DT_R8","DT_CY"), ("DT_R8","DT_NUMERIC"), ("DT_R8","DT_BOOL"),
                ("DT_R4","DT_R8"), ("DT_R4","DT_NUMERIC"),
                ("DT_I2","DT_I4"), ("DT_I4","DT_I2"), ("DT_UI1","DT_I4"), ("DT_I8","DT_I4"),
                ("DT_DATE","DT_DBTIMESTAMP"), ("DT_DATE","DT_DBDATE"),
                ("DT_DBTIMESTAMP","DT_DBDATE"), ("DT_DBDATE","DT_DBTIMESTAMP"),
                ("DT_NUMERIC","DT_CY"), ("DT_CY","DT_NUMERIC"), ("DT_NUMERIC","DT_R8"),
                // string -> scalar: SSIS Data Conversion parses these (a real, supported conversion —
                // not an invented rule). Common when a provider (e.g. Excel/ACE) surfaces cells as text.
                ("DT_WSTR","DT_I4"), ("DT_WSTR","DT_I2"), ("DT_WSTR","DT_I8"), ("DT_WSTR","DT_CY"),
                ("DT_WSTR","DT_NUMERIC"), ("DT_WSTR","DT_R8"), ("DT_WSTR","DT_BOOL"),
                ("DT_WSTR","DT_DBDATE"), ("DT_WSTR","DT_DBTIMESTAMP"),
                ("DT_STR","DT_I4"), ("DT_STR","DT_CY"), ("DT_STR","DT_NUMERIC"), ("DT_STR","DT_DBTIMESTAMP"),
            };
            foreach (var k in known) if (k.from == srcDt && k.to == destDt) return true;
            return null; // unknown -> ambiguity
        }
    }
}
