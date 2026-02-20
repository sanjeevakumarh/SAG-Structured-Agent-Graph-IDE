import * as vscode from 'vscode';
import { WorkflowDefinition, WorkflowInstance, WorkflowStatus, WorkflowStepStatus } from '../client/MessageProtocol';

// ─── Tree item types ────────────────────────────────────────────────────────

export type WorkflowTreeItem =
    | WorkflowGroupItem
    | WorkflowTemplateItem
    | WorkflowRunningItem
    | WorkflowStepItem;

export class WorkflowGroupItem extends vscode.TreeItem {
    constructor(
        public readonly groupId: 'templates' | 'running',
        label: string,
        count: number
    ) {
        super(
            `${label} (${count})`,
            count > 0
                ? vscode.TreeItemCollapsibleState.Expanded
                : vscode.TreeItemCollapsibleState.Collapsed
        );
        this.contextValue = 'workflowGroup';
        this.iconPath = groupId === 'templates'
            ? new vscode.ThemeIcon('library')
            : new vscode.ThemeIcon('run-all');
    }
}

export class WorkflowTemplateItem extends vscode.TreeItem {
    constructor(public readonly definition: WorkflowDefinition) {
        super(definition.name, vscode.TreeItemCollapsibleState.None);
        this.description = definition.isBuiltIn ? 'built-in' : 'workspace';
        this.iconPath = new vscode.ThemeIcon(
            definition.isBuiltIn ? 'symbol-method' : 'file-code',
            new vscode.ThemeColor('charts.blue')
        );
        this.contextValue = 'workflowTemplate';
        this.tooltip = new vscode.MarkdownString(
            `**${definition.name}**\n\n${definition.description}\n\n` +
            `Steps: ${definition.steps.map(s => s.id).join(' → ')}\n\n` +
            `_Click to run this workflow_`
        );
        this.command = {
            command: 'sagIDE.runWorkflow',
            title: 'Run Workflow',
            arguments: [definition],
        };
    }
}

export class WorkflowRunningItem extends vscode.TreeItem {
    constructor(public readonly instance: WorkflowInstance) {
        super(
            instance.definitionName,
            vscode.TreeItemCollapsibleState.Expanded
        );

        const statusIcon = statusToIcon(instance.status);
        const elapsed = elapsedSince(instance.createdAt);
        this.description = `${instance.instanceId.substring(0, 8)} · ${elapsed}`;
        this.iconPath = new vscode.ThemeIcon(
            statusIcon.icon,
            new vscode.ThemeColor(statusIcon.color)
        );
        this.contextValue = instance.status === 'running'
            ? 'workflowRunning'
            : instance.status === 'paused'
                ? 'workflowPaused'
                : 'workflowDone';
        this.tooltip = new vscode.MarkdownString(
            `**${instance.definitionName}** \`${instance.instanceId}\`\n\n` +
            `Status: **${instance.status}**\n` +
            `Started: ${new Date(instance.createdAt).toLocaleString()}\n` +
            (instance.completedAt
                ? `Completed: ${new Date(instance.completedAt).toLocaleString()}\n`
                : '')
        );
        this.command = {
            command: 'sagIDE.openWorkflowGraph',
            title: 'View Workflow Graph',
            arguments: [instance],
        };
    }
}

export class WorkflowStepItem extends vscode.TreeItem {
    constructor(
        public readonly instanceId: string,
        stepId: string,
        stepStatus: WorkflowStepStatus,
        iteration: number,
        issueCount: number
    ) {
        super(stepId, vscode.TreeItemCollapsibleState.None);
        const { icon, color } = stepStatusToIcon(stepStatus);
        this.iconPath = new vscode.ThemeIcon(icon, new vscode.ThemeColor(color));
        this.description = [
            stepStatus,
            iteration > 1 ? `it.${iteration}` : '',
            issueCount > 0 ? `${issueCount} issues` : '',
        ].filter(Boolean).join(' · ');
        this.contextValue = 'workflowStep';
    }
}

// ─── Provider ───────────────────────────────────────────────────────────────

export class WorkflowExplorerProvider implements vscode.TreeDataProvider<WorkflowTreeItem> {
    private _onDidChangeTreeData = new vscode.EventEmitter<WorkflowTreeItem | undefined>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

    private definitions: WorkflowDefinition[] = [];
    private instances: WorkflowInstance[] = [];

    refreshDefinitions(defs: WorkflowDefinition[]): void {
        this.definitions = defs;
        this._onDidChangeTreeData.fire(undefined);
    }

    refreshInstances(instances: WorkflowInstance[]): void {
        this.instances = instances;
        this._onDidChangeTreeData.fire(undefined);
    }

    onWorkflowUpdate(instance: WorkflowInstance): void {
        const idx = this.instances.findIndex(i => i.instanceId === instance.instanceId);
        if (idx >= 0) {
            this.instances[idx] = instance;
        } else {
            this.instances.unshift(instance);
        }
        this._onDidChangeTreeData.fire(undefined);
    }

    getDefinitions(): WorkflowDefinition[] {
        return this.definitions;
    }

    getTreeItem(element: WorkflowTreeItem): vscode.TreeItem {
        return element;
    }

    getChildren(element?: WorkflowTreeItem): WorkflowTreeItem[] {
        if (!element) {
            // Root: two groups
            return [
                new WorkflowGroupItem('templates', 'Templates', this.definitions.length),
                new WorkflowGroupItem('running', 'Running / Recent', this.instances.length),
            ];
        }

        if (element instanceof WorkflowGroupItem) {
            if (element.groupId === 'templates') {
                return this.definitions.map(d => new WorkflowTemplateItem(d));
            }
            // Show running first, then paused, completed/failed
            const sorted = [...this.instances].sort((a, b) => {
                const order: Record<WorkflowStatus, number> = {
                    running: 0, paused: 1, failed: 2, completed: 3, cancelled: 4,
                };
                return (order[a.status] ?? 5) - (order[b.status] ?? 5);
            });
            return sorted.map(i => new WorkflowRunningItem(i));
        }

        if (element instanceof WorkflowRunningItem) {
            const inst = element.instance;
            return Object.values(inst.stepExecutions).map(se =>
                new WorkflowStepItem(inst.instanceId, se.stepId, se.status, se.iteration, se.issueCount)
            );
        }

        return [];
    }
}

// ─── Helpers ────────────────────────────────────────────────────────────────

function statusToIcon(status: WorkflowStatus): { icon: string; color: string } {
    switch (status) {
        case 'running':   return { icon: 'loading~spin',  color: 'charts.blue' };
        case 'paused':    return { icon: 'debug-pause',   color: 'charts.yellow' };
        case 'completed': return { icon: 'check',         color: 'charts.green' };
        case 'failed':    return { icon: 'error',         color: 'charts.red' };
        case 'cancelled': return { icon: 'circle-slash',  color: 'disabledForeground' };
    }
}

function stepStatusToIcon(status: WorkflowStepStatus): { icon: string; color: string } {
    switch (status) {
        case 'pending': return { icon: 'clock', color: 'disabledForeground' };
        case 'running': return { icon: 'loading~spin', color: 'charts.blue' };
        case 'completed': return { icon: 'pass', color: 'charts.green' };
        case 'failed': return { icon: 'error', color: 'charts.red' };
        case 'skipped': return { icon: 'debug-step-over', color: 'disabledForeground' };
    }
}

function elapsedSince(isoDate: string): string {
    const seconds = Math.floor((Date.now() - new Date(isoDate).getTime()) / 1000);
    if (seconds < 60) { return `${seconds}s`; }
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) { return `${minutes}m`; }
    const hours = Math.floor(minutes / 60);
    return `${hours}h`;
}
