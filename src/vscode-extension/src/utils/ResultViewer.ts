import * as vscode from 'vscode';
import { TaskStatusResponse } from '../client/MessageProtocol';

/** Opens the LLM output for a completed task in a markdown editor pane beside the current one. */
export async function openTaskResult(status: TaskStatusResponse): Promise<void> {
    const output = status.result?.output ?? 'No output available';
    const latencySec = status.result?.latencyMs
        ? ` · ${(status.result.latencyMs / 1000).toFixed(1)}s`
        : '';
    const header = [
        `# ${status.agentType} Result`,
        `**Task:** \`${status.taskId.substring(0, 12)}\`  ` +
        `**Model:** ${status.modelProvider} / ${status.modelId}${latencySec}`,
        `**Completed:** ${status.completedAt ? new Date(status.completedAt).toLocaleString() : 'unknown'}`,
        '',
        '---',
        '',
    ].join('\n');

    const doc = await vscode.workspace.openTextDocument({
        content: header + output,
        language: 'markdown',
    });
    await vscode.window.showTextDocument(doc, {
        preview: true,
        viewColumn: vscode.ViewColumn.Beside,
    });
}
