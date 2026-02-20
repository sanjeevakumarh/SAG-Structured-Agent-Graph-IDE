import * as vscode from 'vscode';
import { TaskStatusResponse, TaskStatus, ModelProvider, AgentType } from '../client/MessageProtocol';

export class TaskTreeItem extends vscode.TreeItem {
    constructor(public readonly task: TaskStatusResponse) {
        super(getTaskLabel(task), vscode.TreeItemCollapsibleState.Collapsed);

        this.description = `${task.modelProvider} · ${task.progress}%`;
        this.tooltip = getTooltip(task);
        this.iconPath = getStatusIcon(task.status);

        if (task.status === 'Running') {
            this.contextValue = 'runningTask';
        } else if (task.status === 'WaitingApproval') {
            this.contextValue = 'waitingApprovalTask';
        } else if (task.status === 'Completed') {
            this.contextValue = 'completedTask';
            // Double-clicking a completed task opens its result
            this.command = {
                command: 'sagIDE.showDiff',
                title: 'View Result',
                arguments: [this],
            };
        } else {
            this.contextValue = 'task';
        }
    }
}

export class TaskDetailItem extends vscode.TreeItem {
    constructor(label: string, detail: string) {
        super(label, vscode.TreeItemCollapsibleState.None);
        this.description = detail;
    }
}

function getTaskLabel(task: TaskStatusResponse): string {
    const agentLabel = getAgentLabel(task.agentType);
    return `${agentLabel} [${task.taskId.substring(0, 8)}]`;
}

function getAgentLabel(type: AgentType): string {
    const labels: Record<AgentType, string> = {
        CodeReview: 'Code Review',
        TestGeneration: 'Test Gen',
        Refactoring: 'Refactor',
        Debug: 'Debug',
        Documentation: 'Docs',
        SecurityReview: 'Security',
    };
    return labels[type] || type;
}

function getStatusIcon(status: TaskStatus): vscode.ThemeIcon {
    const icons: Record<TaskStatus, string> = {
        Queued: 'clock',
        Running: 'sync~spin',
        WaitingApproval: 'question',
        Completed: 'check',
        Failed: 'error',
        Cancelled: 'circle-slash',
    };
    return new vscode.ThemeIcon(icons[status] || 'circle-outline');
}

function getTooltip(task: TaskStatusResponse): vscode.MarkdownString {
    const md = new vscode.MarkdownString();
    md.appendMarkdown(`**${getAgentLabel(task.agentType)}**\n\n`);
    md.appendMarkdown(`- **Status:** ${task.status}\n`);
    md.appendMarkdown(`- **Model:** ${task.modelProvider} / ${task.modelId}\n`);
    md.appendMarkdown(`- **Progress:** ${task.progress}%\n`);
    if (task.statusMessage) {
        md.appendMarkdown(`- **Message:** ${task.statusMessage}\n`);
    }
    md.appendMarkdown(`- **Created:** ${new Date(task.createdAt).toLocaleTimeString()}\n`);
    if (task.completedAt) {
        const duration = new Date(task.completedAt).getTime() - new Date(task.createdAt).getTime();
        md.appendMarkdown(`- **Duration:** ${(duration / 1000).toFixed(1)}s\n`);
    }
    return md;
}
