using SsisMcp.Planner;
using Xunit;

namespace SsisMcp.UnitTests.Planner
{
    /// <summary>Pins the evidence-based type/conversion rules that drive infer-vs-ask decisions.</summary>
    public sealed class SsisTypeTests
    {
        [Theory]
        [InlineData("varchar(50)", "DT_STR", 50)]
        [InlineData("nvarchar(100)", "DT_WSTR", 100)]
        [InlineData("int", "DT_I4", 0)]
        [InlineData("money", "DT_CY", 0)]
        [InlineData("date", "DT_DBDATE", 0)]
        [InlineData("smalldatetime", "DT_DBTIMESTAMP", 0)]
        [InlineData("decimal(10,2)", "DT_NUMERIC", 0)]
        public void ResolveDest_maps_sql_types(string sql, string dt, int len)
        {
            var d = SsisTypes.ResolveDest(sql);
            Assert.Equal(dt, d.Dt);
            if (len > 0) Assert.Equal(len, d.Length);
        }

        [Fact]
        public void Same_type_needs_no_conversion()
            => Assert.False(SsisTypes.NeedsConversion("DT_I4", "DT_I4"));

        [Fact]
        public void Unicode_to_ansi_needs_conversion()
            => Assert.True(SsisTypes.NeedsConversion("DT_WSTR", "DT_STR"));

        [Fact]
        public void Excel_double_to_money_and_int_are_known_conversions()
        {
            Assert.True(SsisTypes.NeedsConversion("DT_R8", "DT_CY"));
            Assert.True(SsisTypes.NeedsConversion("DT_R8", "DT_I4"));
        }

        [Fact]
        public void Unknown_conversion_is_null_so_the_planner_asks()
            => Assert.Null(SsisTypes.NeedsConversion("DT_IMAGE", "DT_I4"));

        [Fact]
        public void Source_type_resolution_is_provider_aware()
        {
            Assert.Equal("DT_R8", SsisTypes.ResolveSource("excel", "Double"));
            Assert.Equal("DT_WSTR", SsisTypes.ResolveSource("access", "String"));
            Assert.Equal("DT_STR", SsisTypes.ResolveSource("sql", "varchar(50)"));
        }
    }
}
