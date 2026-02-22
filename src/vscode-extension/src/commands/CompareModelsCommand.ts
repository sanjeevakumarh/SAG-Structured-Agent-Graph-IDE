import * as vscode from 'vscode';
import * as crypto from 'crypto';
import { ServiceConnection } from '../client/ServiceConnection';
import { AgentType, SubmitTaskRequest } from '../client/MessageProtocol';
import { ComparisonTracker } from '../utils/ComparisonTracker';
import { AGENT_TYPES, getAllModels } from './SubmitTaskCommand';
import { log, logError } from '../utils/Logger';

export async function compareModelsCommand(
    connection: ServiceConnection,
    tracker: ComparisonTracker
): Promise<void> {
    if (!connection.isConnected) {
        vscode.window.showErrorMessage('SAG IDE service is not running. Start the service first.');
        return;
    }

    // 1. Pick agent type
    const agentPick = await vscode.window.showQuickPick(
        AGENT_TYPES.map(a => ({ label: a.label, description: a.description, value: a.value })),
        { placeHolder: 'Select agent type for comparison', title: 'SAG IDE — Compare Models' }
    );
    if (!agentPick) { return; }

    // 2. Multi-select models (2-4)
    const modelItems = (await getAllModels(connection)).map(m => ({
        label: m.label,
        description: m.description,
        picked: false,
        model: m,
    }));

    const picks = await vscode.window.showQuickPick(modelItems, {
        placeHolder: 'Select 2-4 models to compare (Space to toggle)',
        canPickMany: true,
        title: 'SAG IDE — Select Models to Compare',
    });
    if (!picks || picks.length < 2) {
        if (picks) {
            vscode.window.showWarningMessage('Select at least 2 models to compare');
        }
        return;
    }
    if (picks.length > 4) {
        vscode.window.showWarningMessage('Maximum 4 models supported for comparison');
        return;
    }

    // 3. Description
    const activeFile = vscode.window.activeTextEditor?.document.uri.fsPath;
    const description = await vscode.window.showInputBox({
        prompt: `Describe the task to run on ${picks.length} models`,
        value: activeFile ? `Review ${activeFile.split(/[\\/]/).pop()}` : '',
        placeHolder: 'e.g., Review this code for security issues',
    });
    if (!description) { return; }

    // 4. Submit N tasks with the same comparisonGroupId
    const groupId = crypto.randomUUID().replace(/-/g, '').substring(0, 8);
    const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
    const workspacePath = workspaceFolder?.uri.fsPath || '';
    const filePaths = activeFile ? [activeFile] : [];

    tracker.registerGroup(groupId, picks.length, agentPick.value as AgentType, description);

    const requests = picks.map(p => {
        const req: SubmitTaskRequest = {
            agentType: agentPick.value as AgentType,
            modelProvider: p.model.provider,
            modelId: p.model.modelId,
            description,
            filePaths,
            priority: 0,
            comparisonGroupId: groupId,
            metadata: workspacePath ? { workspacePath } : undefined,
        };
        return connection.submitTask(req);
    });

    try {
        log(`Starting comparison group ${groupId}: ${picks.length} models for ${agentPick.label}`);
        await Promise.all(requests);
        const modelNames = picks.map(p => p.model.modelId.split(':')[0]).join(', ');
        vscode.window.showInformationMessage(
            `Comparison ${groupId} started — ${picks.length} models running: ${modelNames}`
        );
    } catch (err) {
        logError('Failed to submit comparison', err);
        vscode.window.showErrorMessage(`Failed to start comparison: ${err}`);
    }
}
