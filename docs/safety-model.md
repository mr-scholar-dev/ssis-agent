# Safety model (planned — Fase 4)

> Not implemented in Milestone 0. This documents the contract the mutation layer will enforce.

## Mandatory pipeline before any package mutation

```
inspect → hash (SHA-256) → backup → temp copy → modify temp → validate → commit | rollback
```

- The original `.dtsx` is **never** modified in place.
- Each operation gets a transaction id, timestamp, before/after hash, and an audit-trail entry.
- If the file changes on disk while the agent is working (hash mismatch): **ABORT**, never overwrite.
- Backups and temp/working copies live under ignored folders (`_backups/`, `_temp/`).

## Agent modes

| Mode | Capabilities |
|---|---|
| READ | inspection only |
| EDIT | modify SSIS, no execution |
| BUILD | modify + validate + execute, no arbitrary DB writes |
| AUTONOMOUS | plan + build + validate + execute + repair + verify |

Destructive SQL tools are **disabled by default** and require explicit permission.

## Secrets

Never in code, commits, fixtures, or logs. Use environment variables / user secrets /
git-ignored local config only.
