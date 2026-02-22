import * as vscode from 'vscode';
import * as path from 'path';
import { ServiceConnection } from '../client/ServiceConnection';
import { AgentType, ModelOption, ModelProvider, SubmitTaskRequest } from '../client/MessageProtocol';
import { log, logError } from '../utils/Logger';
import { pickContext, enumerateFolder } from '../utils/ContextPicker';

// ── Schedule helpers ─────────────────────────────────────────────────────────

function getOvernightHour(): number {
    const raw = vscode.workspace.getConfiguration('sagIDE').get<string>('overnightTime', '23:00');
    return parseInt(raw.split(':')[0] ?? '23', 10);
}

function getTonightAt(hour: number): string {
    const d = new Date();
    d.setHours(hour, 0, 0, 0);
    if (d <= new Date()) { d.setDate(d.getDate() + 1); }
    return d.toISOString();
}

function getTomorrowAt(hour: number): string {
    const d = new Date();
    d.setDate(d.getDate() + 1);
    d.setHours(hour, 0, 0, 0);
    return d.toISOString();
}

async function pickSchedule(): Promise<string | null | undefined> {
    const hour = getOvernightHour();
    const options = [
        { label: '$(play) Now', value: undefined as string | undefined },
        { label: `$(moon) Tonight (${hour}:00)`, value: getTonightAt(hour) },
        { label: '$(sun) Tomorrow morning (8:00)', value: getTomorrowAt(8) },
        { label: '$(calendar) Custom time...', value: 'custom' },
    ];
    const pick = await vscode.window.showQuickPick(options, {
        placeHolder: 'When should this task run?',
        title: 'SAG IDE — Schedule Task',
    });
    if (!pick) { return null; } // cancelled
    if (pick.value !== 'custom') { return pick.value; }

    const input = await vscode.window.showInputBox({
        prompt: 'Enter date/time (e.g. 2026-02-18 23:00)',
        placeHolder: new Date(Date.now() + 3_600_000).toISOString().slice(0, 16).replace('T', ' '),
    });
    if (!input) { return null; }
    const parsed = new Date(input);
    if (isNaN(parsed.getTime())) {
        vscode.window.showWarningMessage('Invalid date — task will run immediately');
        return undefined;
    }
    return parsed.toISOString();
}

// ─────────────────────────────────────────────────────────────────────────────

export const AGENT_TYPES: { label: string; value: AgentType; description: string }[] = [
    { label: 'Code Review',     value: 'CodeReview',     description: 'Security, performance, best practices' },
    { label: 'Test Generation', value: 'TestGeneration', description: 'Generate unit tests' },
    { label: 'Refactoring',     value: 'Refactoring',    description: 'Improve code quality' },
    { label: 'Debug',           value: 'Debug',          description: 'Root cause analysis' },
    { label: 'Documentation',   value: 'Documentation',  description: 'Generate docs' },
];

// Re-export ModelOption from the protocol so callers don't need two imports.
export type { ModelOption } from '../client/MessageProtocol';

/**
 * Returns the full model list from the service (primary) or VS Code settings (offline fallback).
 * Cloud models are included only when the service has API keys configured.
 */
export async function getAllModels(connection: ServiceConnection): Promise<ModelOption[]> {
    if (connection.isConnected) {
        try {
            const resp = await connection.getModels();
            if (resp && resp.models.length > 0) { return resp.models; }
        } catch {
            // fall through to settings fallback
        }
    }
    return getOllamaModelsFromSettings();
}

/** Offline fallback: reads Ollama servers from VS Code settings. */
function getOllamaModelsFromSettings(): ModelOption[] {
    const cfg = vscode.workspace.getConfiguration('sagIDE');
    const servers = cfg.get<ModelOption[]>('ollama.servers');
    if (servers && servers.length > 0) {
        return servers.map(s => ({ ...s, provider: 'ollama' as ModelProvider }));
    }
    const baseUrl = cfg.get<string>('ollama.baseUrl', 'http://localhost:11434');
    const model   = cfg.get<string>('ollama.model',   '');
    if (!model) { return []; }
    return [{
        key: 'ollama-local', label: `Ollama ${model}  [Local]`,
        provider: 'ollama', modelId: model, endpoint: baseUrl,
        description: `Local Ollama (${baseUrl})`,
    }];
}

/** Offline fallback: preferred model key per agent type from sagIDE.taskAffinities setting. */
function getTaskAffinityFromSettings(): Record<string, string> {
    return vscode.workspace.getConfiguration('sagIDE').get<Record<string, string>>('taskAffinities') ?? {};
}

/** Shared picker — returns agent + model selection, or null if cancelled. */
async function pickAgentAndModel(
    connection: ServiceConnection
): Promise<{ agentPick: { label: string; value: AgentType }; model: ModelOption } | null> {
    const agentPick = await vscode.window.showQuickPick(
        AGENT_TYPES.map(a => ({ label: a.label, description: a.description, value: a.value })),
        { placeHolder: 'Select agent type', title: 'SAG IDE — New Task' }
    );
    if (!agentPick) { return null; }

    let allModels: ModelOption[];
    let affinities: Record<string, string>;
    if (connection.isConnected) {
        try {
            const resp = await connection.getModels();
            allModels  = resp?.models ?? getOllamaModelsFromSettings();
            affinities = resp?.affinities ?? getTaskAffinityFromSettings();
        } catch {
            allModels  = getOllamaModelsFromSettings();
            affinities = getTaskAffinityFromSettings();
        }
    } else {
        allModels  = getOllamaModelsFromSettings();
        affinities = getTaskAffinityFromSettings();
    }

    const affinityKey = affinities[agentPick.value];
    const recommended = affinityKey ? allModels.find(m => m.key === affinityKey) : undefined;
    const rest = allModels.filter(m => m.key !== affinityKey);
    const ordered = recommended ? [recommended, ...rest] : allModels;

    const modelPick = await vscode.window.showQuickPick(
        ordered.map(m => ({
            label: m.key === affinityKey ? `$(star-full) ${m.label}  (Recommended)` : m.label,
            description: m.description,
            model: m,
        })),
        { placeHolder: 'Select AI model', title: 'SAG IDE — Choose Model' }
    );
    if (!modelPick) { return null; }

    return { agentPick, model: modelPick.model };
}

export async function submitTaskCommand(connection: ServiceConnection): Promise<void> {
    if (!connection.isConnected) {
        vscode.window.showErrorMessage('SAG IDE service is not running. Start the service first.');
        return;
    }

    const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
    const workspacePath = workspaceFolder?.uri.fsPath || '';

    const pick = await pickAgentAndModel(connection);
    if (!pick) { return; }
    const { agentPick, model } = pick;

    const context = await pickContext(workspacePath || undefined);
    if (!context) { return; }

    // Pre-fill the description for a single file; leave blank for multi-file/folder/no-context
    const singleName = context.filePaths.length === 1 ? path.basename(context.filePaths[0]) : undefined;
    const description = await vscode.window.showInputBox({
        prompt: 'Describe what you want the agent to do',
        placeHolder: singleName
            ? `e.g., Review ${singleName}`
            : 'e.g., Review this code for security issues',
        value: singleName ? `Review ${singleName}` : '',
    });
    if (!description) { return; }

    const scheduledFor = await pickSchedule();
    if (scheduledFor === null) { return; } // cancelled

    const request: SubmitTaskRequest = {
        agentType: agentPick.value,
        modelProvider: model.provider,
        modelId: model.modelId,
        description,
        filePaths: context.filePaths,
        priority: 0,
        metadata: workspacePath ? { workspacePath } : undefined,
        scheduledFor,
        modelEndpoint: model.endpoint,
    };

    try {
        const contextSummary = context.filePaths.length > 0 ? ` on ${context.label}` : '';
        log(`Submitting task: ${agentPick.label} via ${model.label}${contextSummary}${scheduledFor ? ` (scheduled: ${scheduledFor})` : ''}`);
        const result = await connection.submitTask(request);
        if (result) {
            const scheduled = scheduledFor ? ` — runs at ${new Date(scheduledFor).toLocaleTimeString()}` : '';
            vscode.window.showInformationMessage(
                `Task ${result.taskId.substring(0, 8)} queued — ${agentPick.label} via ${model.label}${contextSummary}${scheduled}`
            );
        }
    } catch (err) {
        logError('Failed to submit task', err);
        vscode.window.showErrorMessage(`Failed to submit task: ${err}`);
    }
}

/**
 * Submit a task on files selected in the Explorer.
 * VSCode calls this with (clickedUri, allSelectedUris) from the explorer/context menu.
 */
export async function submitTaskOnFilesCommand(
    connection: ServiceConnection,
    clickedUri: vscode.Uri | undefined,
    allUris: vscode.Uri[] | undefined,
): Promise<void> {
    if (!connection.isConnected) {
        vscode.window.showErrorMessage('SAG IDE service is not running. Start the service first.');
        return;
    }

    // Multi-select provides allUris; single right-click gives only clickedUri
    const uris = allUris && allUris.length > 0 ? allUris : (clickedUri ? [clickedUri] : []);
    if (uris.length === 0) {
        vscode.window.showWarningMessage('No files selected');
        return;
    }

    // Collect files; for folders, enumerate contents recursively (excluding common noise dirs)
    const filePaths: string[] = [];
    for (const uri of uris) {
        try {
            const stat = await vscode.workspace.fs.stat(uri);
            if (stat.type === vscode.FileType.File) {
                filePaths.push(uri.fsPath);
            } else if (stat.type === vscode.FileType.Directory) {
                filePaths.push(...await enumerateFolder(uri));
            }
        } catch {
            // Skip inaccessible URIs silently
        }
    }

    if (filePaths.length === 0) {
        vscode.window.showWarningMessage('No files in selection (folders are not included)');
        return;
    }

    const pick = await pickAgentAndModel(connection);
    if (!pick) { return; }
    const { agentPick, model } = pick;

    const fileNames = filePaths.map(p => p.split(/[\\/]/).pop()).join(', ');
    const description = await vscode.window.showInputBox({
        prompt: `Describe the task for ${filePaths.length} file(s)`,
        value: `${agentPick.label}: ${fileNames}`,
    });
    if (!description) { return; }

    const scheduledFor = await pickSchedule();
    if (scheduledFor === null) { return; } // cancelled

    const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
    const workspacePath = workspaceFolder?.uri.fsPath || '';

    const request: SubmitTaskRequest = {
        agentType: agentPick.value,
        modelProvider: model.provider,
        modelId: model.modelId,
        description,
        filePaths,
        priority: 0,
        metadata: workspacePath ? { workspacePath } : undefined,
        scheduledFor,
        modelEndpoint: model.endpoint,
    };

    try {
        log(`Submitting task on ${filePaths.length} files: ${agentPick.label} via ${model.label}`);
        const result = await connection.submitTask(request);
        if (result) {
            const scheduled = scheduledFor ? ` — runs at ${new Date(scheduledFor).toLocaleTimeString()}` : '';
            vscode.window.showInformationMessage(
                `Task ${result.taskId.substring(0, 8)} queued — ${agentPick.label} on ${filePaths.length} file(s) via ${model.label}${scheduled}`
            );
        }
    } catch (err) {
        logError('Failed to submit task', err);
        vscode.window.showErrorMessage(`Failed to submit task: ${err}`);
    }
}
