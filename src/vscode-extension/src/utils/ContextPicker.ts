import * as vscode from 'vscode';
import * as path from 'path';
import { exec } from 'child_process';
import { promisify } from 'util';

const execAsync = promisify(exec);

/** Files/folders excluded when enumerating a folder or resolving a glob. */
const ENUMERATE_EXCLUDE =
    '{**/node_modules/**,**/.git/**,**/bin/**,**/obj/**,**/.next/**,**/dist/**,**/__pycache__/**,**/.venv/**}';

/** Warn before submitting more than this many files. */
const LARGE_FILE_THRESHOLD = 20;

export interface ContextResult {
    filePaths: string[];
    /** Short human-readable summary used in description hints and status messages. */
    label: string;
}

/**
 * Presents a context-source picker and resolves the user's choice to a list of
 * absolute file paths.  Returns null if the user cancelled or the chosen source
 * produced no files.
 */
export async function pickContext(workspacePath?: string): Promise<ContextResult | null> {
    const activeFile = vscode.window.activeTextEditor?.document.uri.fsPath;

    type Item = vscode.QuickPickItem & { id: string };
    const items: Item[] = [
        {
            label:       '$(file-code) Active file',
            description: activeFile ? path.basename(activeFile) : 'no file open',
            id: 'active',
        },
        {
            label:       '$(files) Open editors',
            description: 'All currently open file tabs',
            id: 'open',
        },
        {
            label:       '$(file-directory) Select folder...',
            description: 'Enumerate all files inside a folder',
            id: 'folder',
        },
        {
            label:       '$(search) Glob pattern...',
            description: 'e.g. src/**/*.cs  or  tests/**/*.py',
            id: 'glob',
        },
        {
            label:       '$(git-commit) Git changes',
            description: 'Staged + unstaged modified files',
            id: 'git',
        },
        {
            label:       '$(dash) No context',
            description: 'Description only — no file attachments',
            id: 'none',
        },
    ];

    const pick = await vscode.window.showQuickPick(items, {
        placeHolder: 'How do you want to scope this task?',
        title: 'SAG IDE — Select Context',
    });
    if (!pick) { return null; }

    switch (pick.id) {
        case 'active':
            return activeFile
                ? { filePaths: [activeFile], label: path.basename(activeFile) }
                : { filePaths: [], label: 'no active file' };

        case 'open':
            return resolveOpenEditors();

        case 'folder':
            return resolveFolder();

        case 'glob':
            return resolveGlob(workspacePath);

        case 'git':
            return resolveGitChanges(workspacePath);

        case 'none':
            return { filePaths: [], label: 'no context' };

        default:
            return null;
    }
}

// ── Source resolvers ──────────────────────────────────────────────────────────

function resolveOpenEditors(): ContextResult | null {
    const paths: string[] = [];
    const seen = new Set<string>();
    for (const group of vscode.window.tabGroups.all) {
        for (const tab of group.tabs) {
            if (tab.input instanceof vscode.TabInputText) {
                const fsPath = tab.input.uri.fsPath;
                if (!seen.has(fsPath)) { seen.add(fsPath); paths.push(fsPath); }
            }
        }
    }
    if (paths.length === 0) {
        vscode.window.showWarningMessage('No file editors are currently open');
        return null;
    }
    return { filePaths: paths, label: `${paths.length} open editor(s)` };
}

async function resolveFolder(): Promise<ContextResult | null> {
    const chosen = await vscode.window.showOpenDialog({
        canSelectFolders: true,
        canSelectFiles:   false,
        canSelectMany:    true,
        openLabel: 'Select folder(s)',
        title:     'SAG IDE — Select Folder',
    });
    if (!chosen || chosen.length === 0) { return null; }

    const paths: string[] = [];
    for (const folder of chosen) {
        paths.push(...await enumerateFolder(folder));
    }

    if (paths.length === 0) {
        vscode.window.showWarningMessage('No files found in the selected folder(s)');
        return null;
    }
    if (!(await confirmIfLarge(paths.length))) { return null; }

    const names = chosen.map(f => path.basename(f.fsPath)).join(', ');
    return { filePaths: paths, label: `${paths.length} files in ${names}` };
}

async function resolveGlob(workspacePath?: string): Promise<ContextResult | null> {
    const pattern = await vscode.window.showInputBox({
        prompt:      'Enter a glob pattern (relative to the workspace root)',
        placeHolder: 'e.g. src/**/*.cs  or  tests/**/*.py  or  **/*.ts',
        title:       'SAG IDE — Glob Pattern',
    });
    if (!pattern) { return null; }

    const anchor = workspacePath
        ? new vscode.RelativePattern(workspacePath, pattern)
        : pattern;

    let files: vscode.Uri[];
    try {
        files = await vscode.workspace.findFiles(anchor, ENUMERATE_EXCLUDE);
    } catch {
        vscode.window.showErrorMessage(`Invalid glob pattern: ${pattern}`);
        return null;
    }

    if (files.length === 0) {
        vscode.window.showWarningMessage(`No files matched: ${pattern}`);
        return null;
    }

    const paths = files.map(u => u.fsPath);
    if (!(await confirmIfLarge(paths.length))) { return null; }

    return { filePaths: paths, label: `${paths.length} files matching "${pattern}"` };
}

async function resolveGitChanges(workspacePath?: string): Promise<ContextResult | null> {
    const cwd = workspacePath ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    if (!cwd) {
        vscode.window.showWarningMessage('No workspace folder is open');
        return null;
    }

    try {
        const [modified, untracked] = await Promise.all([
            execAsync('git diff --name-only HEAD',               { cwd }).catch(() => ({ stdout: '' })),
            execAsync('git ls-files --others --exclude-standard', { cwd }).catch(() => ({ stdout: '' })),
        ]);

        const relPaths = [
            ...modified.stdout.trim().split('\n'),
            ...untracked.stdout.trim().split('\n'),
        ].filter(Boolean);

        if (relPaths.length === 0) {
            vscode.window.showInformationMessage('No git changes detected in the workspace');
            return null;
        }

        const paths = relPaths.map(r => path.join(cwd, r));
        return { filePaths: paths, label: `${paths.length} changed file(s)` };
    } catch {
        vscode.window.showErrorMessage('Failed to read git changes. Make sure this is a git repository.');
        return null;
    }
}

// ── Shared helpers ────────────────────────────────────────────────────────────

/**
 * Enumerate all non-excluded files under a folder URI.
 * Also used by submitTaskOnFilesCommand to handle folder selections from the Explorer.
 */
export async function enumerateFolder(folderUri: vscode.Uri): Promise<string[]> {
    const files = await vscode.workspace.findFiles(
        new vscode.RelativePattern(folderUri, '**/*'),
        ENUMERATE_EXCLUDE,
    );
    return files.map(u => u.fsPath);
}

async function confirmIfLarge(count: number): Promise<boolean> {
    if (count <= LARGE_FILE_THRESHOLD) { return true; }
    const answer = await vscode.window.showWarningMessage(
        `${count} files selected — this may produce a large prompt. Continue?`,
        { modal: true },
        'Continue',
    );
    return answer === 'Continue';
}
