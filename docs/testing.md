# Testing

## Run

```powershell
dotnet test SSIS-Agent-MCP.slnx -c Debug
```

## Current suites (Milestone 0)

- **SsisVersionMapTests** — pure mapping of DTS assembly majors ↔ product years ↔ targetable years.
- **GacScannerTests** — parses version-stamped GAC folder names; empty when absent (uses temp dirs).
- **EnvironmentReportTests** — critical-failure gating and report rendering.

These are deterministic and machine-independent. The live probe (`SsisMcp.EnvProbe`) is an
integration check run manually; its results depend on the host and are reported, not asserted.

## Planned

Integration tests against real SSIS (Phase 1+) and fixtures listed in Fase 26
(`empty-package`, `broken-lineage`, `broken-metadata`, …). Per project policy, a capability is
**not** marked complete on mocks alone — it must be exercised against a real SSIS runtime.
