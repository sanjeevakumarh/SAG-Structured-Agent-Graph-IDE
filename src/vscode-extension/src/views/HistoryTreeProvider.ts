import * as vscode from 'vscode';
import { TaskStatusResponse } from '../client/MessageProtocol';
import { TaskTreeItem, TaskDetailItem } from './TreeItems';

export class HistoryTreeProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
    private _onDidChangeTreeData = new vscode.EventEmitter<vscode.TreeItem | undefined>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

    private history: TaskStatusResponse[] = [];
    private maxHistory = 50;

    getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    getChildren(element?: vscode.TreeItem): vscode.TreeItem[] {
        if (!element) {
            return this.history.map(t => new TaskTreeItem(t));
        }
        if (element instanceof TaskTreeItem) {
            return buildHistoryDetails(element.task);
        }
        return [];
    }

    addCompleted(task: TaskStatusResponse): void {
        const idx = this.history.findIndex(h => h.taskId === task.taskId);
        if (idx >= 0) {
            // Update the existing entry (e.g. Cancelled may arrive twice — once from the
            // immediate CancelTaskAsync broadcast and once from ExecuteTaskAsync's catch block).
            this.history[idx] = task;
        } else {
            this.history.unshift(task);
            if (this.history.length > this.maxHistory) {
                this.history.pop();
            }
        }
        this._onDidChangeTreeData.fire(undefined);
    }

    clear(): void {
        this.history = [];
        this._onDidChangeTreeData.fire(undefined);
    }
}

function buildHistoryDetails(task: TaskStatusResponse): vscode.TreeItem[] {
    const items: vscode.TreeItem[] = [
        new TaskDetailItem('Model', `${task.modelProvider} / ${task.modelId}`),
    ];

    if (task.completedAt && task.createdAt) {
        const durMs = new Date(task.completedAt).getTime() - new Date(task.createdAt).getTime();
        items.push(new TaskDetailItem('Duration', `${(durMs / 1000).toFixed(1)}s`));
    }

    if (task.result?.issues?.length) {
        items.push(new TaskDetailItem('Issues', `${task.result.issues.length} issue(s) found`));
    }
    if (task.result?.changes?.length) {
        items.push(new TaskDetailItem('Changes', `${task.result.changes.length} file change(s)`));
    }
    if (task.result?.output) {
        // Strip markdown code fences and trim to ~200 chars for a readable preview
        const preview = task.result.output
            .replace(/```[\s\S]*?```/g, '[code]')
            .replace(/\s+/g, ' ')
            .trim()
            .substring(0, 200);
        items.push(new TaskDetailItem('Output', preview + (task.result.output.length > 200 ? '…' : '')));
    }
    if (task.statusMessage && task.status !== 'Completed') {
        items.push(new TaskDetailItem('Message', task.statusMessage));
    }

    return items;
}
