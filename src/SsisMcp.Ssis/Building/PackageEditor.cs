using System;
using SsisMcp.Core.Building;
using SsisMcp.Core.Lineage;
using SsisMcp.Core.Safety;
using SsisMcp.Safety;
using SsisMcp.Ssis.Lineage;
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

            LineageRepairReport? repair = null;
            var edit = _tx.Apply(packagePath, tempPath =>
            {
                // 1) build on the temp copy
                var pkg = _svc.Load(tempPath);
                Run(pkg);
                _svc.Save(pkg, tempPath);

                // 2) reload (SSIS reassigns lineage ids here) then repair stale references by
                //    stable identity, so the committed package survives a fresh reload.
                var reloaded = _svc.Load(tempPath);
                var pipe = FindPipeline(reloaded, dataFlowTaskName);
                if (pipe != null)
                {
                    repair = new MetadataLineageEngine().Repair(pipe, RepairMode.SafeRepair, 3);
                    if (repair.Actions.Exists(a => a.Applied)) _svc.Save(reloaded, tempPath);
                }
            }, tool);

            var result = new OperationResult { SafetyState = edit.State.ToString(), BackupPath = edit.BackupPath, Detail = edit.Detail, LineageRepair = repair };
            MapState(result, edit);
            if (result.Succeeded) result.Package = _svc.InspectFile(packagePath);
            return result;
        }

        /// <summary>
        /// Dry-run of a Control Flow batch: loads a throwaway copy, applies the operations in memory,
        /// runs SSIS Validate, and returns the would-be inspection — WITHOUT writing to disk. Same
        /// structural/validation checks as <see cref="Apply"/>'s precheck, surfaced for a client.
        /// </summary>
        public OperationResult PreviewControlFlow(string packagePath, Action<ControlFlowBuilder> operation)
        {
            try
            {
                var pkg = _svc.Load(packagePath);
                operation(new ControlFlowBuilder(pkg, _catalog));
                var valid = _svc.Validate(pkg);
                var result = new OperationResult { SafetyState = "Preview", Detail = "validate=" + valid, Succeeded = valid == Dts.DTSExecResult.Success };
                if (!result.Succeeded) result.ErrorCode = BuilderErrorCode.ValidationFailed.ToString();
                result.Package = _svc.Inspect(pkg);
                return result;
            }
            catch (BuilderException bx) { return OperationResult.Fail(bx.Code, bx.Message); }
            catch (Exception ex) { return OperationResult.Fail(BuilderErrorCode.MutationError, ex.GetType().Name + ": " + ex.Message); }
        }

        /// <summary>
        /// Dry-run of a Data Flow batch on the named DFT: in-memory apply + Validate, no write. Note the
        /// in-memory pipeline is validated pre-reload; <see cref="ApplyDataFlow"/> additionally reloads
        /// and repairs lineage. Use preview to catch structural/config errors early.
        /// </summary>
        public OperationResult PreviewDataFlow(string packagePath, string dataFlowTaskName, Action<DataFlowBuilder> operation)
        {
            var pipelineCatalog = new SsisPipelineComponentCatalog();
            try
            {
                var pkg = _svc.Load(packagePath);
                var pipe = FindPipeline(pkg, dataFlowTaskName)
                    ?? throw new BuilderException(BuilderErrorCode.TaskNotFound, $"Data Flow Task '{dataFlowTaskName}' not found");
                operation(new DataFlowBuilder(pipe, pkg, pipelineCatalog));
                var valid = _svc.Validate(pkg);
                var result = new OperationResult { SafetyState = "Preview", Detail = "validate=" + valid, Succeeded = valid == Dts.DTSExecResult.Success };
                if (!result.Succeeded) result.ErrorCode = BuilderErrorCode.ValidationFailed.ToString();
                result.Package = _svc.Inspect(pkg);
                return result;
            }
            catch (BuilderException bx) { return OperationResult.Fail(bx.Code, bx.Message); }
            catch (Exception ex) { return OperationResult.Fail(BuilderErrorCode.MutationError, ex.GetType().Name + ": " + ex.Message); }
        }

        /// <summary>
        /// Undo: restores the most recent Safety backup over the package (the backup the last committed
        /// Apply/ApplyDataFlow created), then re-inspects. Returns a failed result when there is nothing
        /// to undo. Backups themselves are never deleted.
        /// </summary>
        public OperationResult Undo(string packagePath)
        {
            try
            {
                var restored = new BackupManager().RestoreLatest(packagePath);
                if (!restored)
                    return OperationResult.Fail(BuilderErrorCode.MutationError, "nothing to undo (no backup found)");
                return new OperationResult
                {
                    Succeeded = true,
                    SafetyState = "Undone",
                    Detail = "restored latest backup",
                    Package = _svc.InspectFile(packagePath)
                };
            }
            catch (Exception ex)
            {
                return OperationResult.Fail(BuilderErrorCode.MutationError, ex.GetType().Name + ": " + ex.Message);
            }
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
