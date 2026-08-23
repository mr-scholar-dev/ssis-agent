# 02 · Construir práctica (end-to-end)

Usa el **SSIS MCP** (puedes usar `plan.run`, o las tools paso a paso). Sigue este flujo:

```
Discover → Analyze → Plan → Clarify → Preview → Apply → Layout → Validate → Metadata/Lineage → Execute → Verify
```

Reglas:
- Usa **únicamente tools MCP públicas** (no rutas internas ni builders directos).
- **Respeta exactamente** lo que pidan las instrucciones; no simplifiques ni omitas requisitos.
- Usa **ADO.NET** cuando las instrucciones/el profesor lo requieran (si no, elige el proveedor adecuado).
- **Mappings automáticos solo cuando sean inequívocos** (match de nombre + tipo). Si no, pregunta.
- Aplica **conversiones de tipo** cuando sean necesarias (p. ej. unicode↔ansi, numérico→entero/money).
- **Lookup / Derived Column / Conditional Split solo con evidencia explícita** en las instrucciones.
- Todo cambio pasa por **Safety** (`preview` antes de `apply`).
- **Repara metadata/lineage** solo cuando sea seguro; ante reparación ambigua, **pregunta**.
- **Aplica layout** para que abra limpio en Visual Studio.
- **Verifica datos reales** (row counts, valores, outputs) — no basta `Package.Validate`.

Si en `Clarify` faltan datos, **detente y pregúntame**; no inventes para poder continuar.
Al final: resumen breve de lo construido, lo verificado y lo que quedó pendiente/ambiguo.
