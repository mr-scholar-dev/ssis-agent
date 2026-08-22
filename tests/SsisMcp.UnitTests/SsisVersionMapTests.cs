using System.Linq;
using SsisMcp.Core.Environment;
using Xunit;

namespace SsisMcp.UnitTests
{
    public class SsisVersionMapTests
    {
        [Theory]
        [InlineData(13, "2016")]
        [InlineData(14, "2017")]
        [InlineData(15, "2019")]
        [InlineData(16, "2022")]
        [InlineData(17, "2025")]
        public void ProductYear_maps_known_majors(int major, string expected)
        {
            Assert.Equal(expected, SsisVersionMap.ProductYearForAssemblyMajor(major));
        }

        [Fact]
        public void ProductYear_returns_null_for_unknown_major()
        {
            Assert.Null(SsisVersionMap.ProductYearForAssemblyMajor(99));
        }

        [Fact]
        public void Runtime17_can_target_2016_through_2025()
        {
            var years = SsisVersionMap.TargetableYearsForRuntimeMajor(17);
            Assert.Equal(new[] { "2016", "2017", "2019", "2022", "2025" }, years.ToArray());
        }

        [Fact]
        public void Runtime13_can_only_target_2016()
        {
            var years = SsisVersionMap.TargetableYearsForRuntimeMajor(13);
            Assert.Equal(new[] { "2016" }, years.ToArray());
        }
    }
}
