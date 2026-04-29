# Claude Code con Ollama

Este proyecto queda preparado para usar Claude Code con modelos locales de Ollama usando la integracion oficial `ollama launch claude`.

## Requisitos

- Ollama instalado y activo en `http://127.0.0.1:11434`.
- Claude Code instalado y disponible como `claude`.
- Un modelo local de codigo. Recomendado para programar en este PC: `qwen2.5-coder:3b`.

## Uso interactivo

Desde la raiz del proyecto:

```powershell
.\iniciar-claude-ollama.cmd
```

Tambien puedes elegir un modelo:

```powershell
.\iniciar-claude-ollama.cmd -Model qwen2.5-coder:3b
```

Para una prueba rapida con un modelo muy pequeno:

```powershell
.\iniciar-claude-ollama.cmd -Model qwen3:0.6b -Bare -Prompt "Responde solo OK"
```

## Uso con prompt directo

```powershell
.\iniciar-claude-ollama.cmd -Prompt "Analiza la arquitectura del backend y dime por donde empezar"
```

## Variables usadas por el proyecto

El script lee estas variables desde `.env` cuando existen:

```powershell
OLLAMA_BASE_URL=http://127.0.0.1:11434
OLLAMA_CODE_MODEL=qwen2.5-coder:3b
```

Internamente el lanzador usa:

```powershell
ollama launch claude --model <modelo> --yes
```

## Notas

- `qwen3.5` funciona mejor para Claude Code, pero en este PC no entra con la memoria disponible actual.
- `qwen2.5-coder:3b` es mas liviano y queda como modelo por defecto para programar localmente.
- `qwen3:0.6b` queda instalado para pruebas rapidas, pero no es ideal para trabajo real de codigo.
- Si usas otro modelo, asegurate de que este instalado con `ollama pull <modelo>`.
- El script no sube `.env`, credenciales, logs, base local ni artefactos generados a Git.
