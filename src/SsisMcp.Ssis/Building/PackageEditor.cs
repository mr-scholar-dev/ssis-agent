using System;
using SsisMcp.Core.Building;
using SsisMcp.Core.Safety;
using SsisMcp.Safety;
using Dts = Microsoft.SqlServer.Dts.Runtime;
using Wrapper = Microsoft.SqlServer.Dts.Pipeline.Wrapper;

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

            MapState(result, edit);
            if (result.Succeeded) result.Package = _svc.InspectFile(packagePath);
            return result;
        }

        /// <summary>
        /// Applies a Data Flow operation to the pipeline of the named Data Flow Task, through the
        /// same Safety pipeline. Success is confirmed by reload + re-inspection.
        /// </summary>
        public OperationResult ApplyDataFlow(string packagePath, string dataFlowTaskName,
            Action<DataFlowBuilder> operation, string tool = "dataflow.edit")
        {
            var pipelineCatalog = new SsisPipelineComponentCatalog();

            void Run(Dts.Package pkg)
            {
                var pipe = FindPipeline(pkg, dataFlowTaskName)
                    ?? throw new BuilderException(BuilderErrorCode.TaskNotFound, $"Data Flow Task '{dataFlowTaskName}' not found");
                operation(new DataFlowBuilder(pipe, pkg, pipelineCatalog));
            }

            try
            {
                Run(_svc.Load(packagePath)); // structural precheck (throwaway)
            }
            catch (BuilderException bx) { return OperationResult.Fail(bx.Code, bx.Message); }
            catch (Exception ex) { return OperationResult.Fail(BuilderErrorCode.MutationError, ex.GetType().Name + ": " + ex.Message); }

            var edit = _tx.Apply(packagePath, tempPath =>
            {
                var pkg = _svc.Load(tempPath);
                Run(pkg);
                _svc.Save(pkg, tempPath);
            }, tool);

            var result = new OperationResult { SafetyState = edit.State.ToString(), BackupPath = edit.BackupPath, Detail = edit.Detail };
            MapState(result, edit);
            if (result.Succeeded) result.Package = _svc.InspectFile(packagePath);
            return result;
        }

        private static Wrapper.MainPipe? FindPipeline(Dts.Package pkg, string dftName)
        {
            foreach (var exec in Flatten(pkg.Executables))
                if (exec is Dts.TaskHost th && th.Name == dftName && th.InnerObject is Wrapper.MainPipe pipe)
                    return pipe;
            return null;
        }

        private static System.Collections.Generic.IEnumerable<Dts.Executable> Flatten(Dts.Executables executables)
        {
            foreach (Dts.Executable e in executables)
            {
                yield return e;
                if (e is Dts.IDTSSequence seq)
                    foreach (var c in Flatten(seq.Executables)) yield return c;
            }
        }

        private static void MapState(OperationResult result, EditResult edit)
        {
            switch (edit.State)
            {
                case TransactionState.Committed:
                    result.Succeeded = true;
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
        }
    }
}
