using System.Collections.Generic;
using System.Linq;

namespace SsisMcp.Core.Safety
{
    /// <summary>Outcome of validating a package file. Kept SSIS-agnostic so the Safety layer
    /// does not depend on the SSIS assemblies (validation is injected via <see cref="IPackageValidator"/>).</summary>
    public sealed class ValidationResult
    {
        private ValidationResult(bool isValid, IEnumerable<string> messages)
        {
            IsValid = isValid;
            Messages = messages.ToList();
        }

        public bool IsValid { get; }
        public IReadOnlyList<string> Messages { get; }

        public static ValidationResult Success(params string[] messages) => new ValidationResult(true, messages);
        public static ValidationResult Invalid(params string[] messages) => new ValidationResult(false, messages);
    }

    /// <summary>Validates a package on disk. Implemented in the SSIS layer; faked in unit tests.</summary>
    public interface IPackageValidator
    {
        ValidationResult Validate(string dtsxPath);
    }
}
