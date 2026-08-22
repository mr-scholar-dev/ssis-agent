# MCP tool surface (roadmap)

A coherent, small API — not hundreds of hyper-specific tools. Implemented incrementally.

## Implemented (Milestone 0)
_None exposed over MCP yet._ The environment detector exists as a library + console
(`SsisMcp.EnvProbe`) and will back `environment.detect` when the MCP server (Fase 2) lands.

## Planned surface (read-only first)

```
environment.detect
project.open / project.inspect
package.list / package.inspect
controlflow.inspect / dataflow.inspect
connection.list / component.inspect
```

Then, gated behind the Safety layer and agent modes:

```
package.backup / package.validate / package.execute / package.undo
controlflow.add_task / controlflow.connect
dataflow.add_component / dataflow.configure_component / dataflow.connect
connection.create / connection.test
metadata.inspect / metadata.refresh / metadata.repair
mapping.inspect / mapping.auto_map / mapping.repair
sql.inspect / sql.compare
excel.inspect / access.inspect
requirements.analyze
changes.preview / changes.apply
execution.status / execution.errors / execution.verify
```

All responses are structured DTOs (like `EnvironmentReport`), never free text only.
