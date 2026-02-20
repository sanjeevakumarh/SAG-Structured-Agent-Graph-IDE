import * as vscode from 'vscode';
import { ServiceConnection } from '../client/ServiceConnection';
import { AgentType, ModelProvider, SubmitTaskRequest } from '../client/MessageProtocol';
import { log, logError } from '../utils/Logger';

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

// Task affinity: recommended model per agent type (matches appsettings.json TaskAffinities)
// Current server layout (generic local fleet):
//   local-primary    → qwen2.5-coder:7b-instruct
//   local-secondary  → qwen2.5-coder:7b-instruct
//   local-refactor   → deepseek-coder:6.7b
//   local-docs       → phi3.5:latest
const TASK_AFFINITY: Record<AgentType, string> = {
    CodeReview:     'ollama-local-primary',
    TestGeneration: 'ollama-local-primary',
    Refactoring:    'ollama-refactor',
    Debug:          'ollama-local-primary',
    Documentation:  'ollama-docs',
    SecurityReview: 'ollama-refactor',
};

export interface ModelOption {
    key: string;
    label: string;
    provider: ModelProvider;
    modelId: string;
    description: string;
    endpoint?: string;  // explicit Ollama server URL — bypasses routing table
}

export const ALL_MODELS: ModelOption[] = [
        // ── Local LAN models (private, free, no internet) ──────────────────────
        { key: 'ollama-refactor',  label: 'DeepSeek Coder 7B  [Local · Refactor]',
            provider: 'ollama', modelId: 'deepseek-coder:6.7b',
            endpoint: 'http://local-refactor:11434',
            description: 'Fast local — refactoring node' },
        { key: 'ollama-local-primary',   label: 'Qwen2.5 Coder 7B-Instruct  [Local · Primary]',
            provider: 'ollama', modelId: 'qwen2.5-coder:7b-instruct',
            endpoint: 'http://localhost:11434',
            description: 'Code review, debug, tests (primary)' },
        { key: 'ollama-local-secondary', label: 'Qwen2.5 Coder 7B-Instruct  [Local · Secondary]',
            provider: 'ollama', modelId: 'qwen2.5-coder:7b-instruct',
            endpoint: 'http://local-secondary:11434',
            description: 'Code review, debug, tests (secondary)' },
        { key: 'ollama-docs',  label: 'Phi 3.5  [Local · Docs]',
            provider: 'ollama', modelId: 'phi3.5:latest',
            endpoint: 'http://local-docs:11434',
            description: 'Lightweight local — documentation node' },
    // ── Cloud models ────────────────────────────────────────────────────────
    { key: 'gemini', label: 'Gemini 2.0 Flash  [Cloud]',
      provider: 'gemini', modelId: 'gemini-2.0-flash',
      description: 'Google — refactoring, large context' },
    { key: 'codex',  label: 'GPT-4o  [Cloud]',
      provider: 'codex', modelId: 'gpt-4o',
      description: 'OpenAI — test generation, documentation' },
    { key: 'claude', label: 'Claude Sonnet 4.5  [Cloud]',
      provider: 'claude', modelId: 'claude-sonnet-4-5-20250929',
      description: 'Anthropic — best reasoning, complex debug' },
];

/** Shared picker — returns agent + model selection, or null if cancelled. */
async function pickAgentAndModel(): Promise<{ agentPick: { label: string; value: AgentType }; model: ModelOption } | null> {
    const agentPick = await vscode.window.showQuickPick(
        AGENT_TYPES.map(a => ({ label: a.label, description: a.description, value: a.value })),
        { placeHolder: 'Select agent type', title: 'SAG IDE — New Task' }
    );
    if (!agentPick) { return null; }

    const affinityKey = TASK_AFFINITY[agentPick.value];
    const recommended = ALL_MODELS.find(m => m.key === affinityKey);
    const rest = ALL_MODELS.filter(m => m.key !== affinityKey);
    const ordered = recommended ? [recommended, ...rest] : ALL_MODELS;

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

    const pick = await pickAgentAndModel();
    if (!pick) { return; }
    const { agentPick, model } = pick;

    const activeFile = vscode.window.activeTextEditor?.document.uri.fsPath;
    const description = await vscode.window.showInputBox({
        prompt: 'Describe what you want the agent to do',
        placeHolder: activeFile
            ? `e.g., Review ${activeFile.split(/[\\/]/).pop()}`
            : 'e.g., Review this code for security issues',
        value: activeFile ? `Review ${activeFile.split(/[\\/]/).pop()}` : '',
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
        filePaths: activeFile ? [activeFile] : [],
        priority: 0,
        metadata: workspacePath ? { workspacePath } : undefined,
        scheduledFor,
        modelEndpoint: model.endpoint,
    };

    try {
        log(`Submitting task: ${agentPick.label} via ${model.label}${scheduledFor ? ` (scheduled: ${scheduledFor})` : ''}`);
        const result = await connection.submitTask(request);
        if (result) {
            const scheduled = scheduledFor ? ` — runs at ${new Date(scheduledFor).toLocaleTimeString()}` : '';
            vscode.window.showInformationMessage(
                `Task ${result.taskId.substring(0, 8)} queued — ${agentPick.label} via ${model.label}${scheduled}`
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

    // Filter to files only; skip directories
    const filePaths: string[] = [];
    for (const uri of uris) {
        try {
            const stat = await vscode.workspace.fs.stat(uri);
            if (stat.type === vscode.FileType.File) {
                filePaths.push(uri.fsPath);
            }
        } catch {
            // Skip inaccessible URIs silently
        }
    }

    if (filePaths.length === 0) {
        vscode.window.showWarningMessage('No files in selection (folders are not included)');
        return;
    }

    const pick = await pickAgentAndModel();
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
