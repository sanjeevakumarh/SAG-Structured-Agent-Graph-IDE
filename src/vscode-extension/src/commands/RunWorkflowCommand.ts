import * as vscode from 'vscode';
import { ServiceConnection } from '../client/ServiceConnection';
import { WorkflowDefinition, StartWorkflowRequest } from '../client/MessageProtocol';
import { WorkflowGraphPanel } from '../views/WorkflowGraphPanel';
import { ALL_MODELS } from './SubmitTaskCommand';

export async function runWorkflowCommand(
    context: vscode.ExtensionContext,
    connection: ServiceConnection,
    preselectedDef?: WorkflowDefinition
): Promise<void> {
    if (!connection.isConnected) {
        vscode.window.showErrorMessage('SAG IDE service is not running');
        return;
    }

    // 1. Load available workflow definitions
    const workspacePath = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;

    let definitions: WorkflowDefinition[];
    try {
        definitions = await connection.getWorkflows(workspacePath);
    } catch (err) {
        vscode.window.showErrorMessage(`Failed to load workflows: ${err}`);
        return;
    }

    if (definitions.length === 0) {
        vscode.window.showWarningMessage('No workflow definitions found. Check .agentide/workflows/ in your workspace.');
        return;
    }

    // 2. Pick workflow definition (skip if already pre-selected from tree click)
    let definition = preselectedDef;
    if (!definition) {
        const pick = await vscode.window.showQuickPick(
            definitions.map(d => ({
                label: d.name,
                description: d.isBuiltIn ? '$(library) built-in' : '$(file-code) workspace',
                detail: d.description,
                value: d,
            })),
            {
                placeHolder: 'Select a workflow to run',
                title: 'SAG IDE — Run Workflow',
                matchOnDetail: true,
            }
        );
        if (!pick) { return; }
        definition = pick.value;
    }

    // 3. Collect required parameter values
    const inputs: Record<string, string> = {};
    for (const param of definition.parameters ?? []) {
        const value = await vscode.window.showInputBox({
            prompt: `Value for parameter: ${param.name}`,
            placeHolder: param.default ?? `Enter ${param.type} value`,
            value: param.default ?? '',
            title: `SAG Workflow — ${definition.name}`,
        });
        if (value === undefined) { return; } // user cancelled
        inputs[param.name] = value;
    }

    // 4. Select default model (used for steps that don't specify their own model)
    const modelPick = await vscode.window.showQuickPick(
        ALL_MODELS.map(m => ({
            label: m.label,
            description: m.description,
            value: m,
        })),
        {
            placeHolder: 'Select default model (used for steps without explicit model)',
            title: `SAG Workflow — ${definition.name}`,
        }
    );
    if (!modelPick) { return; }
    const model = modelPick.value;

    // 5. Determine file paths — use open editor files or workspace root
    let filePaths: string[] = [];
    const activeEditor = vscode.window.activeTextEditor;
    if (activeEditor && !activeEditor.document.isUntitled) {
        filePaths = [activeEditor.document.uri.fsPath];
    }

    // 6. Submit workflow
    try {
        const req: StartWorkflowRequest = {
            definitionId: definition.id,
            inputs,
            filePaths,
            defaultModelId: model.modelId,
            defaultModelProvider: model.provider,
            modelEndpoint: model.endpoint,
            workspacePath,
        };

        const { instanceId } = await connection.startWorkflow(req);
        vscode.window.showInformationMessage(`$(run-all) Workflow "${definition.name}" started`);

        // 7. Open graph panel — wait briefly for the engine to register the instance
        setTimeout(async () => {
            try {
                const instances = await connection.getWorkflowInstances();
                const instance = instances.find(i => i.instanceId === instanceId);
                if (instance) {
                    WorkflowGraphPanel.show(context, instance, definition!);
                }
            } catch {
                // Graph panel is optional — don't block the user
            }
        }, 300);

    } catch (err) {
        vscode.window.showErrorMessage(`Failed to start workflow: ${err}`);
    }
}
