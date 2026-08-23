# Classroom / Exam Prompts

Prompts reutilizables para usar **SSIS Agent MCP** en clase. Son generalistas (no atados a ninguna
práctica ni dominio) y funcionan igual con **Claude Code** y **Codex**, porque ambos hablan con el
**mismo servidor MCP** (`ssis`). Registra el MCP una vez (ver el README principal / `docs/mcp-install.md`).

| Archivo | Para qué |
|---|---|
| [01-analizar-practica.md](01-analizar-practica.md) | Analizar (solo lectura): descubrir, leer, inspeccionar, clasificar decisiones, preguntar. |
| [02-construir-practica.md](02-construir-practica.md) | Construir end-to-end (Discover→…→Verify). |
| [03-corregir-paquete.md](03-corregir-paquete.md) | Diagnosticar y reparar un `.dtsx` existente. |
| [04-revisar-antes-entregar.md](04-revisar-antes-entregar.md) | Auditoría previa a la entrega (sin modificar). |
| [05-modo-examen.md](05-modo-examen.md) | Prompt principal durante examen/práctica (máxima exactitud). |
| [06-prompt-rapido.md](06-prompt-rapido.md) | Versión corta de una línea. |

## Cómo usarlos

Abre el cliente **en la carpeta de tu práctica** (la que tiene instrucciones + SQL/Excel/Access) para
que el agente pueda descubrir los archivos. Luego pega el contenido del prompt que quieras, o refiérete
al archivo.

### Claude Code
```
# opción A: referir al archivo
Sigue las instrucciones de prompts/05-modo-examen.md para esta práctica.

# opción B: pegar el contenido del prompt directamente
```
En Claude Code también puedes definir slash-commands copiando estos archivos a `.claude/commands/`.

### Codex
```
codex "Sigue las instrucciones de prompts/05-modo-examen.md para esta práctica."

# o pegando el contenido:
codex "$(cat prompts/06-prompt-rapido.md)"
```

## Notas
- Todos exigen: **no inventar**, **preguntar ante ambigüedad**, **preview→Safety**, y **verificar datos
  reales** (no solo `Package.Validate`).
- Indica en tu mensaje el **destino** (conexión/BD) y, si el profesor lo pide, **ADO.NET**; el resto lo
  infiere el agente o lo pregunta.
