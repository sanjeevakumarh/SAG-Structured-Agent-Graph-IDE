import * as vscode from 'vscode';
import { TaskStatusResponse } from '../client/MessageProtocol';

export class ComparisonPanel {
    static show(
        context: vscode.ExtensionContext,
        description: string,
        agentType: string,
        tasks: TaskStatusResponse[]
    ): void {
        const panel = vscode.window.createWebviewPanel(
            'sagIDEComparison',
            `Comparison: ${agentType}`,
            vscode.ViewColumn.One,
            { enableScripts: true, retainContextWhenHidden: true }
        );

        panel.webview.html = ComparisonPanel.buildHtml(description, agentType, tasks);

        // Handle clipboard copy requests from the webview
        panel.webview.onDidReceiveMessage(
            async (message: { command: string; text: string }) => {
                if (message.command === 'copy') {
                    await vscode.env.clipboard.writeText(message.text);
                }
            },
            undefined,
            context.subscriptions
        );
    }

    private static buildHtml(
        description: string,
        agentType: string,
        tasks: TaskStatusResponse[]
    ): string {
        const columns = tasks.map(t => {
            const label = `${t.modelProvider} / ${t.modelId}`;
            const latency = t.result?.latencyMs
                ? `${(t.result.latencyMs / 1000).toFixed(1)}s`
                : 'N/A';
            const output = t.result?.output
                ?? (t.status === 'Failed' ? `FAILED: ${t.statusMessage ?? 'unknown error'}` : 'No output');
            const escaped = output.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
            const jsonSafe = output.replace(/\\/g, '\\\\').replace(/`/g, '\\`');

            return `
            <div class="column">
                <div class="col-header">
                    <span class="model-label">${label}</span>
                    <span class="latency-badge">${latency}</span>
                    <button class="copy-btn" onclick="copyColumn(this)">Copy</button>
                </div>
                <pre class="output" data-raw="\`${jsonSafe}\`">${escaped}</pre>
            </div>`;
        }).join('');

        return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline';">
<style>
  body {
    font-family: var(--vscode-font-family);
    font-size: var(--vscode-font-size);
    color: var(--vscode-foreground);
    background: var(--vscode-editor-background);
    margin: 0; padding: 12px;
  }
  h2 { margin: 0 0 4px 0; font-size: 1.1em; }
  .subtitle { color: var(--vscode-descriptionForeground); font-size: 0.85em; margin-bottom: 14px; }
  .columns { display: flex; gap: 12px; overflow-x: auto; align-items: flex-start; }
  .column {
    flex: 1; min-width: 280px;
    border: 1px solid var(--vscode-panel-border);
    border-radius: 4px;
  }
  .col-header {
    display: flex; align-items: center; gap: 8px;
    padding: 8px 10px;
    background: var(--vscode-sideBar-background);
    border-bottom: 1px solid var(--vscode-panel-border);
  }
  .model-label { flex: 1; font-weight: bold; font-size: 0.85em; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .latency-badge {
    background: var(--vscode-badge-background);
    color: var(--vscode-badge-foreground);
    padding: 1px 6px; border-radius: 10px; font-size: 0.75em; white-space: nowrap;
  }
  .copy-btn {
    background: var(--vscode-button-secondaryBackground);
    color: var(--vscode-button-secondaryForeground);
    border: none; padding: 2px 8px; border-radius: 3px; cursor: pointer; font-size: 0.78em;
  }
  .copy-btn:hover { background: var(--vscode-button-secondaryHoverBackground); }
  .output {
    margin: 0; padding: 10px;
    white-space: pre-wrap; word-break: break-word;
    font-family: var(--vscode-editor-font-family);
    font-size: 0.85em;
    max-height: 75vh; overflow-y: auto;
    line-height: 1.5;
  }
</style>
</head>
<body>
<h2>Model Comparison — ${agentType}</h2>
<p class="subtitle">${description.replace(/</g, '&lt;')}</p>
<div class="columns">${columns}</div>
<script>
const vsc = acquireVsCodeApi();
function copyColumn(btn) {
    const pre = btn.closest('.column').querySelector('.output');
    const text = eval(pre.getAttribute('data-raw'));
    vsc.postMessage({ command: 'copy', text });
    btn.textContent = 'Copied!';
    setTimeout(() => btn.textContent = 'Copy', 1500);
}
</script>
</body>
</html>`;
    }
}
