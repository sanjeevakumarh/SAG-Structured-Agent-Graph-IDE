import * as vscode from 'vscode';
import { DeadLetterEntry } from '../client/MessageProtocol';

export class DlqTreeProvider implements vscode.TreeDataProvider<DlqTreeItem> {
    private _onDidChangeTreeData = new vscode.EventEmitter<DlqTreeItem | undefined>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

    private entries: DeadLetterEntry[] = [];

    refresh(entries: DeadLetterEntry[]): void {
        this.entries = entries;
        this._onDidChangeTreeData.fire(undefined);
    }

    get entryCount(): number {
        return this.entries.length;
    }

    getTreeItem(element: DlqTreeItem): vscode.TreeItem {
        return element;
    }

    getChildren(): DlqTreeItem[] {
        if (this.entries.length === 0) {
            return [];
        }

        return this.entries.map(entry => new DlqTreeItem(entry));
    }
}

export class DlqTreeItem extends vscode.TreeItem {
    constructor(public readonly entry: DeadLetterEntry) {
        super(
            `${entry.agentType} on ${entry.modelProvider}`,
            vscode.TreeItemCollapsibleState.None
        );

        const failedDate = new Date(entry.failedAt);
        const ago = getTimeAgo(failedDate);

        this.description = `${entry.id.substring(0, 8)} — ${ago}`;
        this.iconPath = new vscode.ThemeIcon('error', new vscode.ThemeColor('errorForeground'));
        this.contextValue = 'dlqEntry';

        this.tooltip = new vscode.MarkdownString(
            `**Dead Letter Entry** \`${entry.id}\`\n\n` +
            `| | |\n|---|---|\n` +
            `| Agent | ${entry.agentType} |\n` +
            `| Model | ${entry.modelProvider} / ${entry.modelId} |\n` +
            `| Error | ${entry.error} |\n` +
            `| Code | ${entry.errorCode || 'N/A'} |\n` +
            `| Retries | ${entry.retryCount} |\n` +
            `| Failed | ${failedDate.toLocaleString()} |\n` +
            `| Original Task | \`${entry.originalTaskId.substring(0, 8)}\` |`
        );
    }
}

function getTimeAgo(date: Date): string {
    const seconds = Math.floor((Date.now() - date.getTime()) / 1000);
    if (seconds < 60) { return `${seconds}s ago`; }
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) { return `${minutes}m ago`; }
    const hours = Math.floor(minutes / 60);
    if (hours < 24) { return `${hours}h ago`; }
    const days = Math.floor(hours / 24);
    return `${days}d ago`;
}
