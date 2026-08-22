# Safety model (Fase 4 — implemented)

Implemented in `SsisMcp.Safety`. The original `.dtsx` is **never** mutated in place. Every change
runs through `TransactionalPackageEditor`:

```
hash original (SHA-256)
        ↓
acquire lock  ──► Busy  (another transaction holds the package)
        ↓
copy to temp working file  (<package>.<opId>.tmp, same folder)
        ↓
mutate temp  ──► Failed  (mutation threw; original untouched)
        ↓
validate temp (injected IPackageValidator) ──► Failed  (rollback; original untouched)
        ↓
re-hash original; changed since start? ──► Aborted  (external/user edit preserved)
        ↓
backup original (_backups/…) → copy temp over original → Committed
```

`Preview` runs everything up to validation and returns the would-be hash **without** writing the
original (`PreviewOnly`). `Apply` commits.

## Components

| Type | Role | MCP tool(s) |
|---|---|---|
| `FileHasher` | SHA-256 change detection | — |
| `BackupManager` | timestamped, never-overwritten backups under `_backups/` | `package.backup` / `package.restore` / `package.undo` (`RestoreLatest`) |
| `PackageLock` | cross-process advisory lock via exclusive `<pkg>.lock` sidecar | — |
| `TransactionalPackageEditor` | the pipeline above | `changes.preview` / `changes.apply` / `changes.rollback` |
| `IAuditTrail` / `FileSystemAuditTrail` | append-only JSONL audit (Fase 25) | — |

## Transaction states

`Committed`, `PreviewOnly`, `RolledBack`, `Aborted`, `Busy`, `Failed`. All are recorded to the
audit trail with operation id, timestamps, before/after hashes, backup path and validation outcome.

## SSIS independence

The Safety layer depends only on `SsisMcp.Core` — never on the SSIS assemblies. Validation is
injected via `IPackageValidator`; the SSIS-backed implementation (`SsisPackageValidator`) lives in
`SsisMcp.Ssis`. Unit tests use a fake validator; integration tests use the real one.

## Agent modes (enforcement lands with the MCP server)

| Mode | Capabilities |
|---|---|
| READ | inspection only |
| EDIT | modify SSIS, no execution |
| BUILD | modify + validate + execute, no arbitrary DB writes |
| AUTONOMOUS | plan + build + validate + execute + repair + verify |

Destructive SQL tools are disabled by default and require explicit permission.

## Secrets

Never in code, commits, fixtures, or logs. Environment variables / user secrets / git-ignored
local config only.
