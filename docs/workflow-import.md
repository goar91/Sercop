# Importacion de workflows

## Opcion UI

1. Abre n8n.
2. Ve a `Workflows`.
3. Usa `Import from File`.
4. Importa cada JSON de la carpeta `workflows/`.

## Opcion CLI

Con el stack levantado, ejecuta:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\import-workflows.ps1
```

## Credenciales que debes asociar despues de importar

- `Postgres`: para lectura y escritura operativa.
- `SMTP`: para `Email Send`.

## Orden recomendado

1. `01_sercop_ocds_poller.json`
2. `02_sercop_nco_poller.json`
3. `03_sercop_manual_analysis.json`
4. `04_sercop_chat.json`
5. `05_programming_chat.json`
6. `06_feedback_weekly.json`
