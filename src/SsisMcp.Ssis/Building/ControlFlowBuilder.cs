using System;
using System.Collections.Generic;
using SsisMcp.Core.Building;
using Dts = Microsoft.SqlServer.Dts.Runtime;

namespace SsisMcp.Ssis.Building
{
    /// <summary>
    /// Mutates an in-memory <see cref="Dts.Package"/> (a Safety-layer temp copy). All operations use
    /// the SSIS Object Model, detect collisions/missing targets up front, and throw
    /// <see cref="BuilderException"/> with a structured code. Never writes the .dtsx directly —
    /// callers run it inside <c>TransactionalPackageEditor</c> via <see cref="PackageEditor"/>.
    ///
    /// Both the package and containers are <c>IDTSSequence</c>, so tasks are addressable at any depth
    /// and precedence constraints are created in the scope that owns the two tasks.
    /// </summary>
    public sealed class ControlFlowBuilder
    {
        private readonly Dts.Package _pkg;
        private readonly ISsisComponentCatalog _catalog;

        public ControlFlowBuilder(Dts.Package pkg, ISsisComponentCatalog? catalog = null)
        {
            _pkg = pkg ?? throw new ArgumentNullException(nameof(pkg));
            _catalog = catalog ?? new SsisComponentCatalog();
        }

        /// <summary>Adds a task/container of the given logical kind. Returns its stable ID (GUID).</summary>
        public string AddTask(string logicalKey, string name, string? parentName = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new BuilderException(BuilderErrorCode.InvalidPrecedence, "task name is required");
            if (!_catalog.TryResolveTask(logicalKey, out var creationName))
                throw new BuilderException(BuilderErrorCode.Unsupported, $"unknown task kind '{logicalKey}'");
            if (Find(name) != null)
                throw new BuilderException(BuilderErrorCode.NameCollision, $"a task named '{name}' already exists");

            var scope = ResolveScope(parentName);
            Dts.Executable exec;
            try { exec = scope.Executables.Add(creationName); }
            catch (Exception ex)
            {
                throw new BuilderException(BuilderErrorCode.Unsupported,
                    $"runtime cannot create '{logicalKey}' ({creationName}): {ex.GetType().Name}: {ex.Message}");
            }

            SetName(exec, name);
            return IdOf(exec);
        }

        /// <summary>Configures an Execute SQL Task. Returns which properties the runtime accepted.</summary>
        public ExecuteSqlConfigResult ConfigureExecuteSql(
            string name, string? connection = null, string? sqlStatement = null,
            int? resultSetType = null, int? sqlSourceType = null, bool? bypassPrepare = null, int? timeoutSeconds = null)
        {
            var th = RequireTaskHost(name);
            var result = new ExecuteSqlConfigResult();
            if (connection != null) Apply(th, "Connection", connection, result);
            if (sqlStatement != null) Apply(th, "SqlStatementSource", sqlStatement, result);
            if (sqlSourceType.HasValue) Apply(th, "SqlStatementSourceType", sqlSourceType.Value, result);
            if (resultSetType.HasValue) Apply(th, "ResultSetType", resultSetType.Value, result);
            if (bypassPrepare.HasValue) Apply(th, "BypassPrepare", bypassPrepare.Value, result);
            if (timeoutSeconds.HasValue) Apply(th, "TimeOut", timeoutSeconds.Value, result);
            return result;
        }

        /// <summary>Sets a single task property (best-effort against host then inner object).</summary>
        public void SetTaskProperty(string name, string propertyName, object value)
        {
            var th = RequireTaskHost(name);
            var r = new ExecuteSqlConfigResult();
            Apply(th, propertyName, value, r);
            if (r.Failed.Contains(propertyName))
                throw new BuilderException(BuilderErrorCode.MutationError, $"could not set property '{propertyName}'");
        }

        public void RenameTask(string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName))
                throw new BuilderException(BuilderErrorCode.InvalidPrecedence, "new name is required");
            var exec = Find(oldName) ?? throw new BuilderException(BuilderErrorCode.TaskNotFound, $"task '{oldName}' not found");
            if (!string.Equals(oldName, newName, StringComparison.Ordinal) && Find(newName) != null)
                throw new BuilderException(BuilderErrorCode.NameCollision, $"a task named '{newName}' already exists");
            SetName(exec, newName);
        }

        public void RemoveTask(string name, bool force = false)
        {
            var located = FindWithOwner(name) ?? throw new BuilderException(BuilderErrorCode.TaskNotFound, $"task '{name}' not found");
            var owner = located.owner;

            var dependents = new List<Dts.PrecedenceConstraint>();
            foreach (Dts.PrecedenceConstraint pc in owner.PrecedenceConstraints)
                if (NameOf(pc.PrecedenceExecutable) == name || NameOf(pc.ConstrainedExecutable) == name)
                    dependents.Add(pc);

            if (dependents.Count > 0 && !force)
                throw new BuilderException(BuilderErrorCode.HasDependents,
                    $"task '{name}' has {dependents.Count} precedence constraint(s); remove them first or force");

            foreach (var pc in dependents) RemoveConstraint(owner, pc);
            owner.Executables.Remove(located.exec);
        }

        public void Connect(string fromName, string toName,
            PrecedenceValue value = PrecedenceValue.Success,
            PrecedenceEval eval = PrecedenceEval.Constraint,
            string? expression = null)
        {
            if (string.Equals(fromName, toName, StringComparison.Ordinal))
                throw new BuilderException(BuilderErrorCode.InvalidPrecedence, "cannot connect a task to itself");

            var from = FindWithOwner(fromName) ?? throw new BuilderException(BuilderErrorCode.TaskNotFound, $"source task '{fromName}' not found");
            var to = FindWithOwner(toName) ?? throw new BuilderException(BuilderErrorCode.TaskNotFound, $"target task '{toName}' not found");
            if (OwnerId(from.owner) != OwnerId(to.owner))
                throw new BuilderException(BuilderErrorCode.InvalidPrecedence, "tasks are in different containers; precedence must be within one scope");

            var owner = from.owner;
            foreach (Dts.PrecedenceConstraint existing in owner.PrecedenceConstraints)
                if (NameOf(existing.PrecedenceExecutable) == fromName && NameOf(existing.ConstrainedExecutable) == toName)
                    throw new BuilderException(BuilderErrorCode.InvalidPrecedence, $"'{fromName}' -> '{toName}' is already connected");

            var needsExpression = eval != PrecedenceEval.Constraint;
            if (needsExpression && string.IsNullOrWhiteSpace(expression))
                throw new BuilderException(BuilderErrorCode.InvalidExpression, "an expression is required for this evaluation operation");

            var pc = owner.PrecedenceConstraints.Add(from.exec, to.exec);
            pc.Value = MapValue(value);
            pc.EvalOp = MapEval(eval);
            if (needsExpression) pc.Expression = expression;
        }

        public void Disconnect(string fromName, string toName)
        {
            var from = FindWithOwner(fromName) ?? throw new BuilderException(BuilderErrorCode.TaskNotFound, $"source task '{fromName}' not found");
            var owner = from.owner;
            foreach (Dts.PrecedenceConstraint pc in owner.PrecedenceConstraints)
                if (NameOf(pc.PrecedenceExecutable) == fromName && NameOf(pc.ConstrainedExecutable) == toName)
                {
                    RemoveConstraint(owner, pc);
                    return;
                }
            throw new BuilderException(BuilderErrorCode.InvalidPrecedence, $"no constraint '{fromName}' -> '{toName}' exists");
        }

        // --- helpers ---

        private Dts.IDTSSequence ResolveScope(string? parentName)
        {
            if (string.IsNullOrEmpty(parentName)) return _pkg;
            var parent = Find(parentName!);
            if (parent is Dts.IDTSSequence seq) return seq;
            throw new BuilderException(BuilderErrorCode.TaskNotFound, $"container '{parentName}' not found or is not a container");
        }

        private Dts.TaskHost RequireTaskHost(string name)
        {
            var exec = Find(name) ?? throw new BuilderException(BuilderErrorCode.TaskNotFound, $"task '{name}' not found");
            if (exec is Dts.TaskHost th) return th;
            throw new BuilderException(BuilderErrorCode.MutationError, $"'{name}' is not a configurable task");
        }

        private Dts.Executable? Find(string name) => FindWithOwner(name)?.exec;

        private (Dts.Executable exec, Dts.IDTSSequence owner)? FindWithOwner(string name) =>
            FindIn(_pkg, name);

        private static (Dts.Executable exec, Dts.IDTSSequence owner)? FindIn(Dts.IDTSSequence scope, string name)
        {
            foreach (Dts.Executable exec in scope.Executables)
            {
                if (NameOf(exec) == name) return (exec, scope);
                if (exec is Dts.IDTSSequence child)
                {
                    var nested = FindIn(child, name);
                    if (nested != null) return nested;
                }
            }
            return null;
        }

        private static void Apply(Dts.TaskHost th, string prop, object value, ExecuteSqlConfigResult result)
        {
            try { th.Properties[prop].SetValue(th, value); result.Applied.Add(prop); return; }
            catch { /* try inner */ }
            try { th.Properties[prop].SetValue(th.InnerObject, value); result.Applied.Add(prop); }
            catch { result.Failed.Add(prop); }
        }

        private static void RemoveConstraint(Dts.IDTSSequence owner, Dts.PrecedenceConstraint pc)
        {
            for (var i = 0; i < owner.PrecedenceConstraints.Count; i++)
                if (owner.PrecedenceConstraints[i].ID == pc.ID)
                {
                    owner.PrecedenceConstraints.Remove(i);
                    return;
                }
        }

        private static string OwnerId(Dts.IDTSSequence owner) =>
            owner is Dts.DtsContainer c ? c.ID : "";

        private static string NameOf(Dts.Executable exec)
        {
            if (exec is Dts.TaskHost th) return th.Name;
            if (exec is Dts.DtsContainer c) return c.Name;
            return "";
        }

        private static string IdOf(Dts.Executable exec)
        {
            if (exec is Dts.TaskHost th) return th.ID;
            if (exec is Dts.DtsContainer c) return c.ID;
            return "";
        }

        private static void SetName(Dts.Executable exec, string name)
        {
            if (exec is Dts.TaskHost th) th.Name = name;
            else if (exec is Dts.DtsContainer c) c.Name = name;
        }

        private static Dts.DTSExecResult MapValue(PrecedenceValue v)
        {
            switch (v)
            {
                case PrecedenceValue.Success: return Dts.DTSExecResult.Success;
                case PrecedenceValue.Failure: return Dts.DTSExecResult.Failure;
                default: return Dts.DTSExecResult.Completion;
            }
        }

        private static Dts.DTSPrecedenceEvalOp MapEval(PrecedenceEval e)
        {
            switch (e)
            {
                case PrecedenceEval.Expression: return Dts.DTSPrecedenceEvalOp.Expression;
                case PrecedenceEval.ExpressionAndConstraint: return Dts.DTSPrecedenceEvalOp.ExpressionAndConstraint;
                case PrecedenceEval.ExpressionOrConstraint: return Dts.DTSPrecedenceEvalOp.ExpressionOrConstraint;
                default: return Dts.DTSPrecedenceEvalOp.Constraint;
            }
        }
    }

    /// <summary>Reports which task properties the runtime accepted / rejected during configuration.</summary>
    public sealed class ExecuteSqlConfigResult
    {
        public List<string> Applied { get; } = new List<string>();
        public List<string> Failed { get; } = new List<string>();
    }
}
