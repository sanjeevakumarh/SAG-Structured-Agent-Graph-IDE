import * as vscode from 'vscode';

let outputChannel: vscode.OutputChannel;

export function initLogger(): vscode.OutputChannel {
    outputChannel = vscode.window.createOutputChannel('SAG IDE');
    return outputChannel;
}

export function log(message: string): void {
    const timestamp = new Date().toISOString().substring(11, 19);
    outputChannel?.appendLine(`[${timestamp}] ${message}`);
}

export function logError(message: string, error?: unknown): void {
    const timestamp = new Date().toISOString().substring(11, 19);
    const errorMsg = error instanceof Error ? error.message : String(error ?? '');
    outputChannel?.appendLine(`[${timestamp}] ERROR: ${message} ${errorMsg}`);
}

export function getOutputChannel(): vscode.OutputChannel {
    return outputChannel;
}
