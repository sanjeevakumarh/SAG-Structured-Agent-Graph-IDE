import * as vscode from 'vscode';
import { TaskStatusResponse, Issue } from '../client/MessageProtocol';

export class DiagnosticsManager implements vscode.Disposable {
    private collection: vscode.DiagnosticCollection;

    constructor() {
        this.collection = vscode.languages.createDiagnosticCollection('sagIDE');
    }

    updateFromTask(status: TaskStatusResponse): void {
        if (status.status !== 'Completed' || !status.result) { return; }

        if (status.result.issues?.length > 0) {
            // Structured issues were parsed server-side
            this.applyIssues(status.result.issues);
        } else if (status.result.output) {
            // Fall back to client-side parsing of the raw LLM output
            this.applyFromMetadata({ response: status.result.output });
        }
    }

    applyIssues(issues: Issue[]): void {
        // Group issues by file
        const byFile = new Map<string, vscode.Diagnostic[]>();

        for (const issue of issues) {
            const uri = issue.filePath ? vscode.Uri.file(issue.filePath) : undefined;
            if (!uri) { continue; }

            const key = uri.toString();
            if (!byFile.has(key)) {
                byFile.set(key, []);
            }

            const line = Math.max(0, (issue.line || 1) - 1);
            const range = new vscode.Range(line, 0, line, Number.MAX_SAFE_INTEGER);

            const diagnostic = new vscode.Diagnostic(
                range,
                issue.message,
                mapSeverity(issue.severity)
            );
            diagnostic.source = 'SAG IDE';
            if (issue.suggestedFix) {
                diagnostic.message += `\n\nSuggested fix: ${issue.suggestedFix}`;
            }

            byFile.get(key)!.push(diagnostic);
        }

        // Apply to collection
        for (const [uriStr, diagnostics] of byFile) {
            this.collection.set(vscode.Uri.parse(uriStr), diagnostics);
        }
    }

    applyFromMetadata(metadata: Record<string, string>): void {
        // Parse issues from task metadata (response JSON)
        const response = metadata?.response;
        if (!response) { return; }

        try {
            // Try to find JSON block in the response
            const jsonMatch = response.match(/```json\s*\n([\s\S]*?)```/);
            if (!jsonMatch) { return; }

            const parsed = JSON.parse(jsonMatch[1]);
            if (parsed.issues && Array.isArray(parsed.issues)) {
                const issues: Issue[] = parsed.issues.map((i: any) => ({
                    filePath: i.filePath || i.file || '',
                    line: i.line || 0,
                    severity: i.severity || 'Medium',
                    message: i.message || i.description || '',
                    suggestedFix: i.suggestedFix || i.fix,
                }));
                this.applyIssues(issues);
            }
        } catch {
            // Silently ignore parse errors
        }
    }

    clearForFile(filePath: string): void {
        this.collection.delete(vscode.Uri.file(filePath));
    }

    clearAll(): void {
        this.collection.clear();
    }

    dispose(): void {
        this.collection.dispose();
    }
}

function mapSeverity(severity: string): vscode.DiagnosticSeverity {
    switch (severity?.toLowerCase()) {
        case 'critical':
        case 'high':
            return vscode.DiagnosticSeverity.Error;
        case 'medium':
        case 'moderate':
            return vscode.DiagnosticSeverity.Warning;
        case 'low':
        case 'minor':
            return vscode.DiagnosticSeverity.Information;
        case 'info':
        case 'note':
            return vscode.DiagnosticSeverity.Hint;
        default:
            return vscode.DiagnosticSeverity.Warning;
    }
}
