import * as vscode from 'vscode';
import { TaskStatusResponse } from '../client/MessageProtocol';
import { TaskTreeItem } from './TreeItems';

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
        return [];
    }

    addCompleted(task: TaskStatusResponse): void {
        this.history.unshift(task);
        if (this.history.length > this.maxHistory) {
            this.history.pop();
        }
        this._onDidChangeTreeData.fire(undefined);
    }

    clear(): void {
        this.history = [];
        this._onDidChangeTreeData.fire(undefined);
    }
}
