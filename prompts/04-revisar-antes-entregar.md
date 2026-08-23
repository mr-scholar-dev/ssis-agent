# 04 · Revisar antes de entregar (auditoría, no modificar)

Usa el **SSIS MCP** en modo **solo lectura**. **No modifiques nada** hasta mostrarme la auditoría.

Revisa contra las **instrucciones originales**:
- Control Flow y **precedence constraints**;
- todos los **Data Flow Tasks** y sus pipelines internos;
- **sources / destinations**;
- **mappings**, **Data Conversion**, **Derived Column**, **Conditional Split**, **Lookup**;
- **Connection Managers** y el uso de **ADO.NET / OLE DB** según lo pedido;
- **layout**, **metadata**, **lineage**;
- **ejecución**, **row counts**, y outputs (**Excel / XML / reportes**).

Compara lo construido con lo que exigen las instrucciones y **clasifica cada hallazgo**:
```
Critical   (rompe la ejecución o falta un requisito central)
Must Fix   (incorrecto pero no rompe todo)
Warning    (mejorable / riesgo)
OK         (cumple)
```

Entrega la tabla de hallazgos primero. **No apliques cambios hasta que yo lo apruebe.**
