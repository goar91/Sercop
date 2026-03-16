# Base tecnica

Proyecto local con n8n, PostgreSQL, Qdrant, Ollama y ngrok.

Modelos:
- qwen3:0.6b para analisis general en esta maquina.
- qwen2.5-coder:0.5b para soporte de programacion en esta maquina.
- nomic-embed-text para embeddings.

Servicios:
- n8n en localhost:5678.
- Ollama en localhost:11434.
- Qdrant en localhost:6333.
- PostgreSQL en localhost:5432.


Nota operativa:
- Los webhooks de chat estan respondiendo con RAG + reglas porque la inferencia local de Ollama supera el tiempo razonable en este PC.

