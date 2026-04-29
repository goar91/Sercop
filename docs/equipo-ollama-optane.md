# Equipo, Ollama e Intel Optane

## Resumen del equipo

- Modelo: HP Laptop 14-cf2xxx
- CPU: Intel Core i5-10210U, 8 hilos
- RAM fisica: 12 GB instalados, en modulos de 8 GB + 4 GB
- Capacidad maxima reportada por la placa: 32 GB
- GPU visible: Intel UHD Graphics, aproximadamente 1 GB
- Almacenamiento principal: Intel HBRPEKNX0101AH, 256 GB
- Intel Optane visible: Intel HBRPEKNX0101AHO, aproximadamente 14.4 GB, sin particiones

## Que significa el Optane para Ollama

El Intel Optane de 16 GB aparece como un disco/capa de almacenamiento por RAID, no como memoria RAM ni VRAM. Puede acelerar lecturas de disco o funcionar como cache del sistema Intel RST, pero no aumenta la memoria donde Ollama carga los pesos del modelo.

Para modelos grandes, Ollama necesita RAM fisica y, si existe, VRAM de una GPU compatible. La memoria virtual puede evitar algunos cierres por falta de memoria, pero si el modelo pagina a disco el rendimiento cae mucho y el equipo puede quedarse muy lento.

## Configuracion elegida

El proyecto queda configurado para usar el modelo pequeno por defecto:

```env
OLLAMA_CODE_MODEL=qwen3:0.6b
OLLAMA_CONTEXT_LENGTH=4096
OLLAMA_NUM_PARALLEL=1
OLLAMA_MAX_LOADED_MODELS=1
```

Esta configuracion busca estabilidad en este equipo. Para tareas de programacion mas serias se puede ejecutar manualmente:

```powershell
.\iniciar-ollama.cmd -Model qwen2.5-coder:3b
```

## Sobre qwen3.5

El modelo instalado `qwen3.5` es de 9.7B parametros y esta cuantizado como Q4_K_M. En este equipo puede fallar porque requiere mas memoria libre de la disponible, especialmente con VS Code, Docker, navegador o servicios en segundo plano abiertos.

Recomendacion de RAM:

- 16 GB: minimo para intentar `qwen3.5` con contexto bajo.
- 24 GB: usable para programacion local con contexto moderado.
- 32 GB: recomendado para trabajar con menos bloqueos.

La mejora mas importante para este equipo es ampliar RAM, no depender del Optane.
