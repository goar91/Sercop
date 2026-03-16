# Google Sheets y OAuth para n8n

Esta guia deja n8n conectado a la cuenta Google que usara la hoja operativa del CRM.

## 1. Prepara la hoja de Google

1. Entra con la cuenta Google que vas a enlazar a n8n.
2. Crea un archivo nuevo en Google Sheets.
3. Renombra el archivo, por ejemplo: `CRM HDM SERCOP`.
4. Crea estas pestanas exactas:
   - `oportunidades`
   - `keywords_include`
   - `keywords_exclude`
   - `supplier_catalog`
   - `feedback`
5. Si quieres cargar datos base, importa los CSV de `config\seeds\`.
6. Copia el ID de la hoja desde la URL.

Ejemplo:

- URL: `https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWxYz/edit#gid=0`
- ID: `1AbCdEfGhIjKlMnOpQrStUvWxYz`

## 2. Crea o reutiliza un proyecto en Google Cloud

1. Ve a `https://console.cloud.google.com/`.
2. Arriba a la izquierda, abre el selector de proyectos.
3. Crea un proyecto nuevo o usa uno existente para HDM.
4. Deja ese proyecto seleccionado antes de seguir.

## 3. Habilita las APIs necesarias

1. En Google Cloud ve a `APIs y servicios`.
2. Haz clic en `Habilitar APIs y servicios`.
3. Busca y habilita estas APIs:
   - `Google Sheets API`
   - `Google Drive API`

Nota: n8n documenta que para Google Sheets tambien debes habilitar Google Drive API.

## 4. Configura la pantalla de consentimiento OAuth

1. Ve a `Google Auth Platform > Branding`.
2. Completa:
   - `App name`: por ejemplo `HDM n8n`
   - `User support email`: tu correo de Google
   - `Developer contact information`: tu correo
3. Ve a `Audience`.
4. Elige una opcion:
   - `Internal` si usas Google Workspace y todo quedara dentro de tu dominio
   - `External` si vas a autorizar con cuentas fuera del dominio
5. Si elegiste `External`, agrega en `Test users` la cuenta que vas a usar para autorizar n8n.
6. Guarda los cambios.

## 5. Crea el cliente OAuth para n8n

1. En Google Cloud ve a `Google Auth Platform > Clients`.
2. Haz clic en `Create client`.
3. En `Application type`, elige `Web application`.
4. Ponle un nombre, por ejemplo `n8n local HDM`.
5. Deja abierta esta pantalla porque antes necesitas copiar el redirect URI exacto desde n8n.

## 6. Crea la credencial Google dentro de n8n

1. Abre n8n en `http://localhost:5678`.
2. En el menu lateral entra a `Credentials`.
3. Haz clic en `Create credential`.
4. Busca y elige `Google Sheets OAuth2 API`.
5. n8n te mostrara un campo llamado `OAuth Redirect URL`.
6. Copia ese valor exacto.

Importante:

- No escribas el redirect URI a mano.
- Copialo siempre desde n8n.
- Si luego cambias `localhost` por `ngrok` o un dominio HTTPS, tendras que actualizar ese redirect en Google Cloud.

## 7. Termina el cliente OAuth en Google Cloud

1. Vuelve a la ventana del cliente OAuth en Google Cloud.
2. En `Authorized redirect URIs`, pega el `OAuth Redirect URL` copiado desde n8n.
3. Guarda.
4. Copia estos dos valores:
   - `Client ID`
   - `Client secret`

## 8. Completa la credencial en n8n

1. Vuelve a n8n y pega `Client ID`.
2. Pega `Client Secret`.
3. Si n8n te pide scopes manuales, usa:
   - `https://www.googleapis.com/auth/drive.file https://www.googleapis.com/auth/spreadsheets`
4. Haz clic en `Sign in with Google`.
5. Inicia sesion con la misma cuenta que usara la hoja.
6. Acepta los permisos.
7. Guarda la credencial.

## 9. Comparte la hoja si hace falta

Haz esto solo si la hoja no pertenece a la misma cuenta con la que autorizaste OAuth.

1. Abre la hoja en Google Sheets.
2. Haz clic en `Compartir`.
3. Agrega la cuenta Google con la que autorizaste la credencial en n8n.
4. Dale permiso de `Editor`.

## 10. Configura el proyecto local

Edita `.env` y deja al menos estos valores correctos:

- `GOOGLE_SHEET_ID=<ID de la hoja>`
- `GOOGLE_SHEET_NAME_OPPORTUNITIES=oportunidades`

## 11. Asigna la credencial en los nodos de n8n

1. Abre el workflow que use Google Sheets.
2. Haz clic en el nodo `Google Sheets`.
3. En `Credential to connect with`, elige la credencial que acabas de crear.
4. Guarda el workflow.
5. Repite esto en cada nodo Google Sheets que exista en tus workflows.

## 12. Prueba la conexion

1. En n8n abre un workflow que escriba en `oportunidades`.
2. Ejecuta el nodo manualmente con `Test step` o ejecuta el workflow completo.
3. Verifica que la fila aparezca en Google Sheets.
4. Si falla, revisa:
   - que el `GOOGLE_SHEET_ID` sea correcto
   - que la cuenta autorizada tenga acceso a la hoja
   - que el redirect URI coincida exactamente
   - que `Google Sheets API` y `Google Drive API` esten habilitadas

## 13. Si luego quieres usar Gmail en vez de SMTP

La solucion actual envia correos por SMTP. Si en el futuro quieres mover eso a OAuth de Google:

1. Habilita tambien `Gmail API` en el mismo proyecto.
2. En n8n crea otra credencial: `Gmail OAuth2 API`.
3. Usa el mismo principio: copiar redirect URI desde n8n, pegarlo en Google Cloud y autorizar la cuenta.

## Referencias oficiales

- n8n Google OAuth single service: https://docs.n8n.io/integrations/builtin/credentials/google/oauth-single-service/
- n8n Google OAuth generic: https://docs.n8n.io/integrations/builtin/credentials/google/oauth-generic/
- Google Workspace: habilitar APIs: https://developers.google.com/workspace/guides/enable-apis
- Google Workspace: consent screen: https://developers.google.com/workspace/guides/configure-oauth-consent
- Google Workspace: crear credenciales: https://developers.google.com/workspace/guides/create-credentials
