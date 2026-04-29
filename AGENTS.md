# Instrucciones Para Agentes

## Flujo Codex + Ollama

Este proyecto usa un flujo mixto:

- Ollama local se usa como apoyo para tareas basicas, consultas rapidas, borradores, explicaciones pequenas y comandos sencillos.
- Codex se usa para programacion fuerte y seria: analizar el codigo real, editar archivos, revisar seguridad, ejecutar pruebas, validar cambios, hacer commits y preparar pushes.

## Cuando Usar Ollama

Usa Ollama solo si aporta velocidad o una segunda opinion de bajo riesgo:

- resumir una idea simple;
- generar un primer borrador de texto o comando;
- explicar un error comun;
- comparar opciones pequenas;
- pedir una sugerencia local sin exponer informacion fuera del equipo.

El modelo local por defecto es:

```env
OLLAMA_CODE_MODEL=qwen3:0.6b
OLLAMA_CONTEXT_LENGTH=4096
```

Comando recomendado:

```powershell
.\iniciar-ollama.cmd -Prompt "pregunta corta"
```

## Cuando No Usar Ollama

No delegar en Ollama decisiones criticas ni cambios grandes:

- refactors amplios;
- migraciones de base de datos;
- autenticacion, permisos o seguridad;
- cambios con credenciales o datos sensibles;
- edicion automatica de archivos;
- diagnosticos donde haya que leer varias capas del proyecto;
- decisiones de arquitectura.

En esos casos Codex debe analizar el repositorio directamente y aplicar los cambios.

## Regla De Aplicacion

Ollama puede proponer. Codex decide, implementa y verifica.

Antes de modificar archivos, revisar el contexto local del proyecto. Despues de modificar, validar con comandos o pruebas razonables y reportar el resultado.
