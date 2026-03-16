const vscode = require('vscode');
const http = require('node:http');
const https = require('node:https');
const { URL } = require('node:url');

function activate(context) {
  const output = vscode.window.createOutputChannel('SERCOP Assistant');

  const register = (command, handler) => {
    context.subscriptions.push(vscode.commands.registerCommand(command, () => handler(output)));
  };

  register('sercopAssistant.askQuestion', async (outputChannel) => {
    const question = await vscode.window.showInputBox({
      prompt: 'Pregunta por arquitectura, scripts, backend, frontend o Docker del proyecto',
      placeHolder: 'Ejemplo: explica la integracion entre n8n, CRM, Qdrant y Ollama',
      ignoreFocusOut: true
    });

    if (!question) {
      return;
    }

    await runAssistant({
      output: outputChannel,
      question
    });
  });

  register('sercopAssistant.explainSelection', async (outputChannel) => {
    await runAssistant({
      output: outputChannel,
      question: 'Explica este codigo dentro del proyecto. Resume su objetivo, riesgos, dependencias y mejoras concretas.'
    });
  });

  register('sercopAssistant.improveSelection', async (outputChannel) => {
    await runAssistant({
      output: outputChannel,
      question: 'Mejora este codigo. Propone cambios concretos, razonados y compatibles con el proyecto actual.'
    });
  });

  register('sercopAssistant.fixSelection', async (outputChannel) => {
    await runAssistant({
      output: outputChannel,
      question: 'Revisa este codigo buscando errores, riesgos o regresiones y propone una correccion minima.'
    });
  });
}

async function runAssistant({ output, question }) {
  const config = vscode.workspace.getConfiguration('sercopAssistant');
  const credentials = await resolveCredentials(config);
  if (!credentials) {
    return;
  }

  const editorContext = buildEditorContext(config);
  const baseUrl = String(config.get('baseUrl') || 'http://localhost:5050').replace(/\/+$/, '');
  const payload = {
    question,
    module: 'code',
    filePath: editorContext.filePath,
    language: editorContext.language,
    selection: editorContext.selection,
    codeContext: editorContext.codeContext
  };

  output.clear();
  output.appendLine(`POST ${baseUrl}/api/personal-ai/ask`);
  output.appendLine(`Archivo: ${editorContext.filePath || 'sin archivo activo'}`);
  output.appendLine(`Lenguaje: ${editorContext.language || 'sin lenguaje activo'}`);
  output.appendLine(`Pregunta: ${question}`);
  output.show(true);

  try {
    const response = await postJson(`${baseUrl}/api/personal-ai/ask`, {
      question: payload.question,
      searchMode: 'auto',
      filePath: payload.filePath,
      language: payload.language,
      selection: payload.selection,
      codeContext: payload.codeContext
    }, credentials);
    showResponsePanel(question, editorContext, response);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    output.appendLine(`Error: ${message}`);
    vscode.window.showErrorMessage(`SERCOP Assistant: ${message}`);
  }
}

function buildEditorContext(config) {
  const editor = vscode.window.activeTextEditor;
  const includeEntireFile = Boolean(config.get('includeEntireFileWhenNoSelection', true));
  const maxContextCharacters = Number(config.get('maxContextCharacters', 6000));

  if (!editor) {
    return {
      filePath: null,
      language: null,
      selection: null,
      codeContext: null
    };
  }

  const document = editor.document;
  const selectedText = editor.selection && !editor.selection.isEmpty
    ? document.getText(editor.selection)
    : '';

  let codeContext = selectedText;
  if (!codeContext && includeEntireFile) {
    codeContext = document.getText().slice(0, maxContextCharacters);
  }

  return {
    filePath: document.uri.scheme === 'file' ? document.uri.fsPath : null,
    language: document.languageId || null,
    selection: selectedText || null,
    codeContext: codeContext || null
  };
}

async function resolveCredentials(config) {
  const username = String(config.get('username') || 'admin');
  let password = String(config.get('password') || '');

  if (!password) {
    password = await vscode.window.showInputBox({
      prompt: `Clave Basic Auth para ${username}`,
      password: true,
      ignoreFocusOut: true
    });
  }

  if (!password) {
    vscode.window.showWarningMessage('SERCOP Assistant requiere credenciales del CRM para consultar la API.');
    return null;
  }

  return { username, password };
}

function postJson(urlString, payload, credentials) {
  const url = new URL(urlString);
  const transport = url.protocol === 'https:' ? https : http;
  const body = JSON.stringify(payload);
  const authHeader = Buffer.from(`${credentials.username}:${credentials.password}`, 'utf8').toString('base64');

  return new Promise((resolve, reject) => {
    const request = transport.request({
      method: 'POST',
      hostname: url.hostname,
      port: url.port || (url.protocol === 'https:' ? 443 : 80),
      path: `${url.pathname}${url.search}`,
      headers: {
        Authorization: `Basic ${authHeader}`,
        'Content-Type': 'application/json',
        'Content-Length': Buffer.byteLength(body)
      }
    }, (response) => {
      const chunks = [];

      response.on('data', (chunk) => chunks.push(chunk));
      response.on('end', () => {
        const raw = Buffer.concat(chunks).toString('utf8');
        const parsed = raw ? JSON.parse(raw) : {};

        if (response.statusCode && response.statusCode >= 400) {
          const message = parsed?.detail || parsed?.title || raw || `HTTP ${response.statusCode}`;
          reject(new Error(message));
          return;
        }

        resolve(parsed);
      });
    });

    request.on('error', reject);
    request.write(body);
    request.end();
  });
}

function showResponsePanel(question, editorContext, response) {
  const panel = vscode.window.createWebviewPanel(
    'sercopAssistant',
    'SERCOP Assistant',
    vscode.ViewColumn.Beside,
    {
      enableFindWidget: true
    }
  );

  const sources = Array.isArray(response.sources) ? response.sources : [];
  const sourceList = sources.length === 0
    ? '<p>Sin fuentes recuperadas.</p>'
    : sources.map((source) => `
        <li>
          <strong>${escapeHtml(source.label || 'fuente')}</strong>
          <div>${escapeHtml(source.kind || '')}</div>
          <code>${escapeHtml(source.reference || '')}</code>
        </li>
      `).join('');

  panel.webview.html = `<!DOCTYPE html>
  <html lang="es">
    <head>
      <meta charset="UTF-8">
      <meta name="viewport" content="width=device-width, initial-scale=1.0">
      <title>SERCOP Assistant</title>
      <style>
        :root {
          color-scheme: light dark;
          font-family: "Segoe UI", sans-serif;
        }
        body {
          margin: 0;
          padding: 24px;
          line-height: 1.5;
        }
        h1, h2 {
          margin: 0 0 12px;
        }
        section {
          margin-bottom: 18px;
          padding: 16px;
          border: 1px solid rgba(127, 127, 127, 0.25);
          border-radius: 12px;
        }
        pre, code {
          font-family: Consolas, monospace;
          white-space: pre-wrap;
          word-break: break-word;
        }
        ul {
          margin: 0;
          padding-left: 18px;
        }
      </style>
    </head>
    <body>
      <section>
        <h1>${escapeHtml(response.model || 'Asistente')}</h1>
        <div><strong>Modulo:</strong> ${escapeHtml(response.module || 'code')}</div>
        <div><strong>Archivo:</strong> ${escapeHtml(editorContext.filePath || 'sin archivo activo')}</div>
        <div><strong>Lenguaje:</strong> ${escapeHtml(editorContext.language || 'no informado')}</div>
      </section>

      <section>
        <h2>Pregunta</h2>
        <pre>${escapeHtml(question)}</pre>
      </section>

      <section>
        <h2>Respuesta</h2>
        <pre>${escapeHtml(response.answer || 'Sin respuesta')}</pre>
      </section>

      <section>
        <h2>Contexto</h2>
        <pre>${escapeHtml(response.contextSummary || '')}</pre>
      </section>

      <section>
        <h2>Fuentes</h2>
        <ul>${sourceList}</ul>
      </section>
    </body>
  </html>`;
}

function escapeHtml(value) {
  return String(value || '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

function deactivate() {}

module.exports = {
  activate,
  deactivate
};
