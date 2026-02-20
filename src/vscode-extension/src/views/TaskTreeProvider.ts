import * as vscode from 'vscode';
import { TaskStatusResponse } from '../client/MessageProtocol';
import { TaskTreeItem, TaskDetailItem } from './TreeItems';

export class TaskTreeProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
    private _onDidChangeTreeData = new vscode.EventEmitter<vscode.TreeItem | undefined>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

    private tasks: Map<string, TaskStatusResponse> = new Map();

    getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    getChildren(element?: vscode.TreeItem): vscode.TreeItem[] {
        if (!element) {
            // Root level: show all tasks grouped by status
            const running = this.getTasksByStatus('Running');
            const queued = this.getTasksByStatus('Queued');
            const waiting = this.getTasksByStatus('WaitingApproval');

            return [...running, ...queued, ...waiting];
        }

        if (element instanceof TaskTreeItem) {
            // Show task details as children
            const task = element.task;
            const details: TaskDetailItem[] = [
                new TaskDetailItem('Model', `${task.modelProvider} / ${task.modelId}`),
                new TaskDetailItem('Status', `${task.status} (${task.progress}%)`),
            ];
            if (task.statusMessage) {
                details.push(new TaskDetailItem('Message', task.statusMessage));
            }
            if (task.startedAt) {
                details.push(new TaskDetailItem('Started', new Date(task.startedAt).toLocaleTimeString()));
            }
            return details;
        }

        return [];
    }

    private getTasksByStatus(status: string): TaskTreeItem[] {
        return Array.from(this.tasks.values())
            .filter(t => t.status === status)
            .map(t => new TaskTreeItem(t));
    }

    updateTask(status: TaskStatusResponse): void {
        this.tasks.set(status.taskId, status);
        this._onDidChangeTreeData.fire(undefined);
    }

    removeTask(taskId: string): void {
        this.tasks.delete(taskId);
        this._onDidChangeTreeData.fire(undefined);
    }

    setTasks(tasks: TaskStatusResponse[]): void {
        this.tasks.clear();
        for (const task of tasks) {
            this.tasks.set(task.taskId, task);
        }
        this._onDidChangeTreeData.fire(undefined);
    }

    getTask(taskId: string): TaskStatusResponse | undefined {
        return this.tasks.get(taskId);
    }

    get taskCount(): number {
        return this.tasks.size;
    }

    get runningCount(): number {
        return Array.from(this.tasks.values()).filter(t => t.status === 'Running').length;
    }
}
