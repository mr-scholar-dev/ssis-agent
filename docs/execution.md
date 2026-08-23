# Execution model & ExecutionVerified gate

## Finding: SSIS runs transformation pipelines only in a Microsoft-signed host

Empirically on this machine (SQL 2025 engine = **Enterprise Evaluation**, SQL 2019 = Express; VS 2022
+ SSDT installed):

| Host | Transformation pipeline | Reason |
|---|---|---|
| In-process `Package.Execute` (our net48 process) | ❌ | edition **license gate** ("install Standard Edition of Integration Services") |
| `dtexec.exe` — `C:\Program Files\Microsoft SQL Server\170\DTS\Binn` | ❌ | license gate (IS not installed as a licensed feature) |
| `dtexec.exe` — `…\130\DTS\Binn` | ❌ | license gate |
| `dtexec.exe` — SSDT `…\CommonExtensions\Microsoft\SSIS\170\Binn` | ❌ (not gated) | `DTS.Application` COM **not registered** standalone (`0x80040154`) |
| **VS 2022 (`DtsDebugHost.exe`, IPC-driven by the IDE)** | ✅ | signed + licensed design-time host (observed: exit 0) |

A trivial `source → destination` pipeline runs even unblocked, but **any transform** (Derived Column,
Lookup, …) triggers the gate. So the SSIS runtime executes transform pipelines **only inside a
Microsoft-signed host**: a *licensed* `dtexec`, `DtsDebugHost.exe`, or `devenv.exe`.

## Abstraction

`IPackageExecutionHost` (Core) with `InProcessExecutionHost`, `DtexecExecutionHost`, and
**`SsdtDebugExecutionHost`** (Ssis.Execution). `SsdtDebugExecutionHost` tries the available signed
`dtexec` hosts (licensed SQL Server IS first, then SSDT), runs `/FILE … /REPORTING EW`, and returns a
**structured** `ExecutionResult`:
- `Success` (exit 0) + parsed errors/warnings,
- `Failure` (ran but exit ≠ 0),
- `EnvironmentBlocked` with the precise reason (`license-gated` / `0x80040154` / `no signed dtexec`).

`DestinationDataVerifier` (row counts / scalar / expression) checks the data actually landed — a
green exit code is never treated as proof.

The builder never couples to any of this — execution stays behind `IPackageExecutionHost`.

## ✅ ExecutionVerified = TRUE (2026-08-23)

After installing the **Integration Services** shared feature (engine = Enterprise Evaluation, so its
`dtexec` is licensed), execution is unblocked and **verified with real destination data**:

```
Derived Column:  Monto=100 -> Impuesto=13.00   (rows=1)                  PASS
Lookup:          Match=2, NoMatch=1, TipoCliente(Codigo 1)=Premium        PASS
```

- `environment.detect` → `execution.dataFlow : PASS (available)`.
- `SsdtDebugExecutionHost.Execute` → `Success` via the licensed `dtexec` (v17.0.1000.7), no gate.
- `ExecutionVerifiedTests` assert `Success` + the exact destination data (Impuesto, Match/NoMatch,
  TipoCliente); `ExecutionTests` assert build+validate PASS then execute+verify. Both green.

So for the executed pipelines (OLE DB Source → Derived Column → OLE DB Destination; OLE DB Source →
Lookup → Match/NoMatch destinations):

```
build PASS · validate PASS · execute PASS · destination data verify PASS  => ExecutionVerified = true
```

The tests stay **portable**: on a machine with no licensed signed host, `Execute` returns
`EnvironmentBlocked` (known reason) and the strict test skips.

## How to unblock (one user action, mirrors the SSDT install)

Install the **Integration Services** shared feature from the SQL Server 2025 installer. The engine is
already **Enterprise Evaluation** (licensed), so its `dtexec` will run pipelines **without the gate**.
After that, `SsdtDebugExecutionHost` picks up the licensed `dtexec` and the `ExecutionTests` execute +
verify destination data → **ExecutionVerified**.

(Interactive execution inside VS 2022 already works via `DtsDebugHost`; a headless CI path needs the
licensed `dtexec`. Driving `DtsDebugHost` directly is IPC-only/undocumented and intentionally not used.)
