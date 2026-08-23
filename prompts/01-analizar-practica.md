# 01 · Analizar práctica (solo lectura)

Usa el **SSIS MCP**. NO modifiques ningún `.dtsx` en este paso: es solo análisis.

Haz:
1. **Descubre** todos los archivos disponibles (`files.discover`).
2. **Identifica** por tipo: instrucciones (doc/pdf/docx), SQL, Excel, Access, Flat File, `.dtsx/.dtproj`.
3. **Lee las instrucciones completas** antes de decidir nada.
4. **Inspecciona esquemas** de cada fuente (`sql.inspect`, `excel.inspect`, `access.inspect`) y del
   destino: columnas, tipos, nullability, PK/FK cuando existan.
5. **Clasifica cada decisión** como: `Explicit` · `InferredHigh` · `InferredLow` · `Ambiguous`.
6. **No inventes** mappings, reglas de negocio, Lookups, Derived Columns, Conditional Splits, fechas,
   ni valores por defecto. Si no hay evidencia suficiente en los archivos → márcalo `Ambiguous`.
7. Si hay ambigüedades, **pregúntame antes de construir** (preguntas cortas y concretas).
8. **Propón** (sin aplicar): Control Flow, DFTs, fuentes, transformaciones, destinos, mappings,
   precedencias, Connection Managers y outputs requeridos.

Entrega: la lista de fuentes/esquemas, la propuesta de arquitectura, y una tabla de decisiones
`Explicit/InferredHigh/InferredLow/Ambiguous` con las preguntas pendientes. **No toques el paquete.**
