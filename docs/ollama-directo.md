# Ollama directo

Si Claude Code no funciona bien con Ollama en este equipo, puedes usar Ollama directamente desde la terminal integrada.

## Uso interactivo

Desde la raiz del proyecto:

```powershell
.\iniciar-ollama.cmd
```

Esto abre un chat con el modelo configurado en `.env`:

```env
OLLAMA_CODE_MODEL=qwen3:0.6b
OLLAMA_CONTEXT_LENGTH=4096
```

## Elegir modelo

```powershell
.\iniciar-ollama.cmd -Model qwen3:0.6b
```

Para usar el modelo de codigo mas grande cuando haya memoria libre:

```powershell
.\iniciar-ollama.cmd -Model qwen2.5-coder:3b
```

## Enviar un prompt directo

```powershell
.\iniciar-ollama.cmd -Prompt "Explicame la estructura de este proyecto"
```

## Desde VS Code

Usa `Terminal > Run Task...` y elige:

- `Ollama directo`
- `Ollama directo - prueba rapida`

## Nota

Ollama directo no edita archivos por si solo como Claude Code. Sirve para preguntar, generar codigo, explicar errores o pedir comandos. Para aplicar cambios en el proyecto, copia la respuesta util y ejecuta los comandos o edita los archivos desde el editor.

En este equipo el modelo por defecto es `qwen3:0.6b` para reducir uso de RAM. `qwen3.5` puede fallar si no hay memoria libre suficiente.
