# SERCOP Assistant para VS Code

Extension local para consultar el endpoint `POST /api/personal-ai/ask` del asistente personal desde Visual Studio Code.

## Comandos

- `SERCOP Assistant: Ask Project Question`
- `SERCOP Assistant: Explain Selection`
- `SERCOP Assistant: Improve Selection`
- `SERCOP Assistant: Review Selection For Bugs`

## Configuracion

- `sercopAssistant.baseUrl`
- `sercopAssistant.username`
- `sercopAssistant.password`
- `sercopAssistant.includeEntireFileWhenNoSelection`
- `sercopAssistant.maxContextCharacters`

## Uso

1. Asegura que el CRM este arriba en `http://localhost:5050`.
2. Instala la extension desde esta carpeta o empaquetala como `.vsix`.
3. Abre un archivo del repositorio y ejecuta uno de los comandos.
