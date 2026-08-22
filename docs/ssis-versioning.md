# SSIS versioning & the 2016 compatibility finding

## DTS managed-assembly major → product year

| Assembly major | SQL Server / SSIS year |
|---|---|
| 13 | 2016 |
| 14 | 2017 |
| 15 | 2019 |
| 16 | 2022 |
| 17 | 2025 |

## Finding on the current dev machine

The requested **initial target was SSIS 2016 (assembly v13)**. The environment probe found:

- `Microsoft.SqlServer.ManagedDTS` **v17.0.0.0** in the GAC (SQL Server 2025 family). **No v13.**
- Local SQL Server engine reports version **17.00.1000** (SQL Server 2025).
- Visual Studio **2026 (v18.7.3)** and **2022 (v17.14)**; **SSIS Projects extension not installed**.

### What this means

1. **You cannot bind to SSIS 2016 assemblies here** — they are not present. The core must bind to
   the installed **v17** runtime.
2. **Downlevel packages are still possible** via `TargetServerVersion` (a v17 runtime can, in
   principle, emit `SQLServer2016..SQLServer2025` packages). **This must be proven by actually
   building and loading a 2016-targeted package in Phase 1** — it is not assumed.
3. Components available in a v17 runtime may differ from a genuine 2016 install. The **SSIS adapter
   layer** exists precisely to absorb these differences; the environment probe records the real
   installed major so higher layers never assume 2016.

### Decision needed from the project owner

- **Option A (recommended):** treat the installed **v17** runtime as the build runtime and target
  older engines via `TargetServerVersion`. Validate the 2016 target in Phase 1 before relying on it.
- **Option B:** install SQL Server 2016 Integration Services (adds the v13 assemblies) on a machine
  that matches the real deployment target, if byte-level 2016 fidelity is required.

Until this is decided, the core will bind to v17 and mark generated packages with an explicit,
recorded `TargetServerVersion`. No code pretends 2016 assemblies exist.
