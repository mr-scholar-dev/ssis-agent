# 03 · Corregir / reparar un paquete SSIS existente

Usa el **SSIS MCP** para diagnosticar y reparar un `.dtsx` ya existente. **Diagnostica antes de tocar.**

Fase 1 — diagnóstico (sin modificar):
```
inspect → diagnose → preview → backup/safety
```
Revisa: Control Flow, Data Flows, mappings, connections, metadata, lineage, `VS_NEEDSNEWMETADATA`,
stale lineage, external metadata, Data Conversion, Conditional Split, Lookup, y conexiones ADO.NET.
Usa `package.inspect`, `dataflow.inspect`, `metadata.inspect`, `connection.test`.

Fase 2 — reparación (por Safety):
```
apply → reload → inspect → validate → execute → verify
```
Reglas:
- Cada cambio con **`preview` antes de `apply`**; Safety mantiene backup (y `package.undo` disponible).
- **No recrees componentes** si se pueden rebindear/ajustar (evita destruir trabajo existente).
- Repara lineage por identidad estable cuando sea seguro; si es ambiguo, **pregunta**.
- Al terminar: **valida y ejecuta**, y **verifica datos reales** (no solo exit code / `Package.Validate`).

Entrega: qué estaba mal, qué reparaste, qué verificaste, y qué quedó pendiente.
