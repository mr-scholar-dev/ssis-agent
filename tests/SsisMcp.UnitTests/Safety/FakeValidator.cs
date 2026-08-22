using SsisMcp.Core.Safety;

namespace SsisMcp.UnitTests.Safety
{
    /// <summary>Test double: validity is decided by a flag, not by real SSIS.</summary>
    internal sealed class FakeValidator : IPackageValidator
    {
        private readonly bool _valid;
        public int Calls { get; private set; }

        public FakeValidator(bool valid) => _valid = valid;

        public ValidationResult Validate(string dtsxPath)
        {
            Calls++;
            return _valid ? ValidationResult.Success("fake ok") : ValidationResult.Invalid("fake bad");
        }
    }
}
