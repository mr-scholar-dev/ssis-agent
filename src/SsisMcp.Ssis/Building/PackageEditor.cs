using System;
using SsisMcp.Core.Building;
using SsisMcp.Core.Safety;
using SsisMcp.Safety;

namespace SsisMcp.Ssis.Building
{
    /// <summary>
    /// Applies Control Flow builder operations to a .dtsx STRICTLY through the Safety layer:
    ///
    ///   precheck (structural, no write) → temp copy → mutate → SSIS validate → commit | rollback
    ///   → reload from disk → inspect → return the re-inspected package
    ///
    /// A committed operation is only reported successful after the package has been reloaded and
    /// re-inspected — Package.Validate() alone is never treated as sufficient.
    /// </summary>
    public sealed class PackageEditor
    {
        private readonly PackageService _svc;
        private readonly ISsisComponentCatalog _catalog;
        private readonly TransactionalPackageEditor _tx;

        public PackageEditor(
            PackageService? service = null,
            ISsisComponentCatalog? catalog = null,
            IAuditTrail? audit = null)
        {
            _svc = service ?? new PackageService();
            _catalog = catalog ?? new SsisComponentCatalog();
            _tx = new TransactionalPackageEditor(new SsisPackageValidator(_svc), audit ?? new InMemoryAuditTrail());
        }

        public OperationResult Apply(string packagePath, Action<ControlFlowBuilder> operation, string tool = "controlflow.edit")
        {
            // 1) Structural precheck on a throwaway copy — surfaces collisions/missing/expression
            //    errors as structured codes WITHOUT touching disk or the Safety pipeline.
            try
            {
                var probe = _svc.Load(packagePath);
                operation(new ControlFlowBuilder(probe, _catalog));
            }
            catch (BuilderException bx)
            {
                return OperationResult.Fail(bx.Code, bx.Message);
            }
            catch (Exception ex)
            {
                return OperationResult.Fail(BuilderErrorCode.MutationError, ex.GetType().Name + ": " + ex.Message);
            }

            // 2) Real, transactional write.
            var edit = _tx.Apply(packagePath, tempPath =>
            {
                var pkg = _svc.Load(tempPath);
                operation(new ControlFlowBuilder(pkg, _catalog));
                _svc.Save(pkg, tempPath);
            }, tool);

            var result = new OperationResult
            {
                SafetyState = edit.State.ToString(),
                BackupPath = edit.BackupPath,
                Detail = edit.Detail
            };

            switch (edit.State)
            {
                case TransactionState.Committed:
                    result.Succeeded = true;
                    // 3) Reload from disk and inspect — the authoritative post-condition.
                    result.Package = _svc.InspectFile(packagePath);
                    break;

                case TransactionState.Failed:
                    result.Succeeded = false;
                    result.ErrorCode = (edit.Validation != null && !edit.Validation.IsValid
                        ? BuilderErrorCode.ValidationFailed
                        : BuilderErrorCode.MutationError).ToString();
                    break;

                case TransactionState.Aborted:
                    result.Succeeded = false;
                    result.ErrorCode = BuilderErrorCode.ExternalChange.ToString();
                    break;

                case TransactionState.Busy:
                    result.Succeeded = false;
                    result.ErrorCode = BuilderErrorCode.Busy.ToString();
                    break;

                default:
                    result.Succeeded = false;
                    result.ErrorCode = BuilderErrorCode.MutationError.ToString();
                    break;
            }
            return result;
        }
    }
}
