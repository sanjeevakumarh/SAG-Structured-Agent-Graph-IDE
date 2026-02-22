import * as vscode from 'vscode';
import { AgentType } from '../client/MessageProtocol';

/**
 * Live streaming output panel — one WebView per active task.
 * Receives incremental text chunks via postMessage and appends them efficiently.
 */
export class StreamingOutputPanel {
    private static panels = new Map<string, StreamingOutputPanel>();

    private readonly panel: vscode.WebviewPanel;
    private readonly taskId: string;
    private startTime = Date.now();
    private tokenCount = 0;

    private constructor(context: vscode.ExtensionContext, taskId: string, agentType: AgentType, modelId: string) {
        this.taskId = taskId;
        this.panel = vscode.window.createWebviewPanel(
            'sagStreaming',
            `SAG: ${agentType} · ${modelId.split(':')[0]} ⟳`,
            vscode.ViewColumn.Beside,
            { enableScripts: true, retainContextWhenHidden: true }
        );

        this.panel.webview.html = getHtml();

        this.panel.onDidDispose(() => {
            StreamingOutputPanel.panels.delete(taskId);
        }, null, context.subscriptions);

        this.panel.webview.onDidReceiveMessage(msg => {
            if (msg.command === 'copy') {
                vscode.env.clipboard.writeText(msg.text);
                vscode.window.showInformationMessage('Output copied to clipboard');
            }
        }, null, context.subscriptions);
    }

    /** Create or get the panel for a given task, then send the next chunk. */
    static update(
        context: vscode.ExtensionContext,
        taskId: string,
        agentType: AgentType,
        modelId: string,
        chunk: string,
        tokensGeneratedSoFar: number,
        isLastChunk: boolean
    ): void {
        let instance = StreamingOutputPanel.panels.get(taskId);
        if (!instance) {
            instance = new StreamingOutputPanel(context, taskId, agentType, modelId);
            StreamingOutputPanel.panels.set(taskId, instance);
        }
        instance.tokenCount = tokensGeneratedSoFar;
        instance.panel.webview.postMessage({ command: 'append', text: chunk, done: isLastChunk });

        if (isLastChunk) {
            const elapsed = ((Date.now() - instance.startTime) / 1000).toFixed(1);
            instance.panel.title = `SAG: ${agentType} · ${modelId.split(':')[0]} ✓ (${elapsed}s, ~${tokensGeneratedSoFar} tok)`;
        }
    }

    /** Signal cancellation to the webview — stops the cursor and shows a 'Cancelled' status line. */
    static cancel(taskId: string): void {
        const instance = StreamingOutputPanel.panels.get(taskId);
        if (!instance) { return; }
        instance.panel.webview.postMessage({ command: 'cancelled' });
        instance.panel.title = instance.panel.title.replace('⟳', '✗');
    }

    static closeAll(): void {
        for (const p of StreamingOutputPanel.panels.values()) {
            p.panel.dispose();
        }
        StreamingOutputPanel.panels.clear();
    }
}

function getHtml(): string {
    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline';">
<title>SAG Streaming</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body {
    background: var(--vscode-editor-background);
    color: var(--vscode-editor-foreground);
    font-family: var(--vscode-editor-font-family, 'Consolas', monospace);
    font-size: var(--vscode-editor-font-size, 13px);
    display: flex;
    flex-direction: column;
    height: 100vh;
    overflow: hidden;
  }
  #toolbar {
    display: flex;
    justify-content: flex-end;
    padding: 4px 8px;
    background: var(--vscode-titleBar-activeBackground);
    gap: 6px;
    flex-shrink: 0;
  }
  button {
    background: var(--vscode-button-background);
    color: var(--vscode-button-foreground);
    border: none;
    padding: 3px 10px;
    cursor: pointer;
    border-radius: 2px;
    font-size: 12px;
  }
  button:hover { background: var(--vscode-button-hoverBackground); }
  #output {
    flex: 1;
    overflow-y: auto;
    padding: 12px;
    white-space: pre-wrap;
    word-break: break-word;
    line-height: 1.5;
  }
  #cursor {
    display: inline-block;
    width: 8px;
    height: 1em;
    background: var(--vscode-editor-foreground);
    vertical-align: text-bottom;
    animation: blink 1s step-end infinite;
    opacity: 0.7;
  }
  #cursor.hidden { display: none; }
  @keyframes blink { 50% { opacity: 0; } }
  #status {
    font-size: 11px;
    color: var(--vscode-descriptionForeground);
    padding: 3px 8px;
    flex-shrink: 0;
    border-top: 1px solid var(--vscode-panel-border);
  }
</style>
</head>
<body>
<div id="toolbar">
  <button id="copyBtn">Copy output</button>
  <button id="clearBtn">Clear</button>
</div>
<div id="output"><span id="cursor"></span></div>
<div id="status">Waiting for output...</div>
<script>
  const vscode = acquireVsCodeApi();
  const output = document.getElementById('output');
  const cursor = document.getElementById('cursor');
  const status = document.getElementById('status');
  let fullText = '';
  let chunkCount = 0;

  window.addEventListener('message', event => {
    const { command, text, done } = event.data;
    if (command === 'append') {
      fullText += text;
      chunkCount++;
      // Insert text before the blinking cursor
      const textNode = document.createTextNode(text);
      output.insertBefore(textNode, cursor);
      // Auto-scroll to bottom
      output.scrollTop = output.scrollHeight;
      status.textContent = done
        ? \`Done — \${fullText.length.toLocaleString()} chars received\`
        : \`Receiving... \${fullText.length.toLocaleString()} chars (\${chunkCount} chunks)\`;
      if (done) { cursor.className = 'hidden'; }
    } else if (command === 'cancelled') {
      cursor.className = 'hidden';
      status.textContent = \`Cancelled — \${fullText.length.toLocaleString()} chars received (partial output)\`;
    }
  });

  document.getElementById('copyBtn').addEventListener('click', () => {
    vscode.postMessage({ command: 'copy', text: fullText });
  });

  document.getElementById('clearBtn').addEventListener('click', () => {
    fullText = '';
    chunkCount = 0;
    output.innerHTML = '<span id="cursor"></span>';
    status.textContent = 'Cleared';
  });
</script>
</body>
</html>`;
}
