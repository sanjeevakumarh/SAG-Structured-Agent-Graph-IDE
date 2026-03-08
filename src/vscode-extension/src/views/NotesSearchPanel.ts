import * as vscode from 'vscode';
import { postJson } from '../utils/postJson';
import { fetchJson } from '../utils/fetchJson';

interface NotesSearchResult {
    rank: number;
    file: string;
    path: string;
    score: number;
    lastModified: string | null;
    hasTasks: boolean;
    snippet: string;
}

interface NotesSearchResponse {
    query: string;
    count: number;
    summary?: string;
    results: NotesSearchResult[];
    message?: string;
}

interface NotesStats {
    totalFiles: number;
    totalChunks: number;
    lastIndexTime: string | null;
    filesWithTasks: number;
}

export class NotesSearchPanel {
    private static _panel: vscode.WebviewPanel | undefined;

    static show(context: vscode.ExtensionContext, restBaseUrl: string): void {
        if (NotesSearchPanel._panel) {
            NotesSearchPanel._panel.reveal();
            return;
        }

        const panel = vscode.window.createWebviewPanel(
            'sagIDE.notesSearch',
            'Knowledge Base',
            vscode.ViewColumn.One,
            { enableScripts: true, retainContextWhenHidden: true }
        );
        NotesSearchPanel._panel = panel;

        panel.webview.html = getHtml();

        panel.webview.onDidReceiveMessage(
            async (msg: { command: string; query?: string; topK?: number }) => {
                if (msg.command === 'search' && msg.query) {
                    try {
                        const data = await postJson<NotesSearchResponse>(
                            restBaseUrl, '/api/notes/search',
                            { query: msg.query, topK: msg.topK ?? 10 }
                        );
                        panel.webview.postMessage({ type: 'results', data });
                    } catch (e) {
                        panel.webview.postMessage({
                            type: 'error',
                            message: e instanceof Error ? e.message : String(e),
                        });
                    }
                } else if (msg.command === 'loadStats') {
                    try {
                        const stats = await fetchJson<NotesStats>(restBaseUrl, '/api/notes/stats');
                        panel.webview.postMessage({ type: 'stats', data: stats });
                    } catch {
                        // Silently ignore stats load failure
                    }
                } else if (msg.command === 'reindex') {
                    try {
                        await postJson(restBaseUrl, '/api/notes/reindex', {});
                        panel.webview.postMessage({ type: 'toast', message: 'Reindex triggered' });
                    } catch (e) {
                        panel.webview.postMessage({
                            type: 'error',
                            message: e instanceof Error ? e.message : String(e),
                        });
                    }
                } else if (msg.command === 'openFile') {
                    const filePath = msg.query;
                    if (filePath) {
                        const uri = vscode.Uri.file(filePath);
                        vscode.commands.executeCommand('vscode.open', uri);
                    }
                }
            },
            undefined,
            context.subscriptions
        );

        panel.onDidDispose(() => { NotesSearchPanel._panel = undefined; });
    }
}

function getHtml(): string {
    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8"/>
<style>
    *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
    body { font-family: var(--vscode-font-family); font-size: 13px;
           color: var(--vscode-foreground); background: var(--vscode-editor-background);
           padding: 16px; }

    .search-box { display: flex; gap: 8px; margin-bottom: 12px; }
    .search-box input { flex: 1; padding: 6px 10px; border: 1px solid var(--vscode-input-border);
        background: var(--vscode-input-background); color: var(--vscode-input-foreground);
        border-radius: 4px; font-size: 13px; }
    .search-box input:focus { outline: none; border-color: var(--vscode-focusBorder); }
    .search-box select { padding: 6px; border: 1px solid var(--vscode-input-border);
        background: var(--vscode-input-background); color: var(--vscode-input-foreground);
        border-radius: 4px; }
    .search-box button { padding: 6px 14px; border: none; border-radius: 4px; cursor: pointer;
        background: var(--vscode-button-background); color: var(--vscode-button-foreground);
        font-size: 13px; }
    .search-box button:hover { background: var(--vscode-button-hoverBackground); }

    .stats { font-size: 12px; color: var(--vscode-descriptionForeground); margin-bottom: 12px;
             display: flex; gap: 16px; flex-wrap: wrap; align-items: center; }
    .stats .val { color: var(--vscode-foreground); font-weight: 600;
                  font-family: var(--vscode-editor-font-family); }
    .stats button { font-size: 11px; padding: 2px 8px; border: 1px solid var(--vscode-input-border);
        background: transparent; color: var(--vscode-textLink-foreground); border-radius: 3px;
        cursor: pointer; }

    .result { border: 1px solid var(--vscode-panel-border); border-radius: 6px;
              padding: 12px; margin-bottom: 8px; }
    .result:hover { border-color: var(--vscode-focusBorder); }
    .result-header { display: flex; justify-content: space-between; align-items: center;
                     margin-bottom: 4px; }
    .result-file { font-weight: 600; color: var(--vscode-textLink-foreground); cursor: pointer;
                   font-size: 14px; }
    .result-file:hover { text-decoration: underline; }
    .result-score { font-size: 11px; font-family: var(--vscode-editor-font-family);
                    color: var(--vscode-descriptionForeground); }
    .result-meta { font-size: 11px; color: var(--vscode-descriptionForeground); margin-bottom: 6px; }
    .result-snippet { font-size: 12px; line-height: 1.6; white-space: pre-wrap;
                      word-break: break-word; color: var(--vscode-editor-foreground);
                      max-height: 200px; overflow-y: auto; }
    .task-badge { display: inline-block; font-size: 10px; padding: 1px 5px; border-radius: 3px;
                  background: var(--vscode-testing-iconPassed); color: #fff; margin-left: 6px; }

    .empty { text-align: center; color: var(--vscode-descriptionForeground); padding: 32px;
             font-size: 13px; }
    .error { color: var(--vscode-errorForeground); }
    .toast { position: fixed; bottom: 16px; right: 16px; padding: 8px 14px; border-radius: 4px;
             background: var(--vscode-notificationsInfoIcon-foreground); color: #fff;
             font-size: 12px; opacity: 0; transition: opacity .3s; }
    .toast.show { opacity: 1; }

    .summary { border: 1px solid var(--vscode-focusBorder); border-radius: 6px;
               padding: 14px; margin-bottom: 14px; line-height: 1.7; }
    .summary-label { font-size: 11px; font-weight: 600; color: var(--vscode-focusBorder);
                     text-transform: uppercase; letter-spacing: 0.5px; margin-bottom: 8px; }
    .summary-text { font-size: 13px; white-space: pre-wrap; }
</style>
</head>
<body>

<div class="stats" id="stats"></div>

<div class="search-box">
    <input id="query" type="text" placeholder="Search your knowledge base…"
           autofocus />
    <select id="topk">
        <option value="5">5</option>
        <option value="10" selected>10</option>
        <option value="20">20</option>
    </select>
    <button onclick="doSearch()">Search</button>
</div>

<div id="summary"></div>
<div id="results"><div class="empty">Type a query and press Enter to search your Logseq notes</div></div>
<div class="toast" id="toast"></div>

<script>
const vscode = acquireVsCodeApi();

document.getElementById('query').addEventListener('keydown', e => {
    if (e.key === 'Enter') doSearch();
});

function doSearch() {
    const query = document.getElementById('query').value.trim();
    if (!query) return;
    const topK = parseInt(document.getElementById('topk').value) || 10;
    document.getElementById('summary').innerHTML = '';
    document.getElementById('results').innerHTML = '<div class="empty">Searching…</div>';
    vscode.postMessage({ command: 'search', query, topK });
}

function esc(s) {
    return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
}

function fmtDate(iso) {
    if (!iso) return '—';
    const d = new Date(iso);
    return d.toLocaleDateString(undefined, { year:'numeric', month:'short', day:'numeric' });
}

window.addEventListener('message', event => {
    const msg = event.data;
    const el = document.getElementById('results');

    if (msg.type === 'results') {
        const d = msg.data;
        const sumEl = document.getElementById('summary');
        sumEl.innerHTML = '';
        if (d.message) { el.innerHTML = '<div class="empty">' + esc(d.message) + '</div>'; return; }
        if (!d.results?.length) { el.innerHTML = '<div class="empty">No results found</div>'; return; }

        if (d.summary) {
            sumEl.innerHTML = '<div class="summary">' +
                '<div class="summary-label">AI Summary</div>' +
                '<div class="summary-text">' + esc(d.summary) + '</div>' +
            '</div>';
        }

        el.innerHTML = d.results.map(r => {
            const pct = (r.score * 100).toFixed(1);
            const taskBadge = r.hasTasks ? '<span class="task-badge">TASKS</span>' : '';
            return '<div class="result">' +
                '<div class="result-header">' +
                    '<span><span class="result-file" onclick="openFile(\\'' + esc(r.path.replace(/\\\\/g, '\\\\\\\\').replace(/'/g, "\\\\'")) + '\\')">' + esc(r.file) + '</span>' + taskBadge + '</span>' +
                    '<span class="result-score">' + pct + '% match</span>' +
                '</div>' +
                '<div class="result-meta">Modified: ' + fmtDate(r.lastModified) + ' · ' + esc(r.path) + '</div>' +
                '<div class="result-snippet">' + esc(r.snippet) + '</div>' +
            '</div>';
        }).join('');
    } else if (msg.type === 'error') {
        el.innerHTML = '<div class="empty error">Error: ' + esc(msg.message) + '</div>';
    } else if (msg.type === 'stats') {
        const s = msg.data;
        document.getElementById('stats').innerHTML =
            '<span>Files: <span class="val">' + s.totalFiles + '</span></span>' +
            '<span>Chunks: <span class="val">' + s.totalChunks + '</span></span>' +
            '<span>With tasks: <span class="val">' + s.filesWithTasks + '</span></span>' +
            (s.lastIndexTime ? '<span>Indexed: <span class="val">' + fmtDate(s.lastIndexTime) + '</span></span>' : '') +
            '<button onclick="reindex()">Reindex</button>';
    } else if (msg.type === 'toast') {
        const t = document.getElementById('toast');
        t.textContent = msg.message;
        t.classList.add('show');
        setTimeout(() => t.classList.remove('show'), 3000);
    }
});

function openFile(path) {
    vscode.postMessage({ command: 'openFile', query: path });
}

function reindex() {
    vscode.postMessage({ command: 'reindex' });
}

// Load stats on init
vscode.postMessage({ command: 'loadStats' });
</script>
</body>
</html>`;
}
