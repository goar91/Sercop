# Base tecnica

Proyecto local con n8n, PostgreSQL, Qdrant, Ollama y ngrok.

Modelos:
- qwen2.5:14b para analisis general local.
- qwen2.5-coder:14b para soporte de programacion local.
- nomic-embed-text para embeddings.
- Soporte opcional para OpenAI mediante `AI_PROVIDER=openai` y modelos `gpt-5`.

Servicios:
- n8n en localhost:5678.
- Ollama en localhost:11434.
- Qdrant en localhost:6333.
- PostgreSQL en localhost:5432.


Nota operativa:
- El asistente del CRM consulta tambien una base vectorial con fragmentos reales del repositorio para responder sobre arquitectura, scripts y codigo.

