using System;
using SsisMcp.Core.Safety;
using Dts = Microsoft.SqlServer.Dts.Runtime;

namespace SsisMcp.Ssis
{
    /// <summary>
    /// SSIS-backed <see cref="IPackageValidator"/>: loads the .dtsx and runs the real
    /// <c>Package.Validate</c>. This is the bridge that lets the SSIS-agnostic Safety layer
    /// validate real packages without depending on the SSIS assemblies itself.
    /// </summary>
    public sealed class SsisPackageValidator : IPackageValidator
    {
        private readonly PackageService _service;

        public SsisPackageValidator(PackageService? service = null)
        {
            _service = service ?? new PackageService();
        }

        public ValidationResult Validate(string dtsxPath)
        {
            try
            {
                var pkg = _service.Load(dtsxPath);
                var result = _service.Validate(pkg);
                return result == Dts.DTSExecResult.Success
                    ? ValidationResult.Success($"Validate returned {result}")
                    : ValidationResult.Invalid($"Validate returned {result}");
            }
            catch (Exception ex)
            {
                return ValidationResult.Invalid($"load/validate threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
