using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SAGIDE.Service.Infrastructure;

public class GitService
{
    private readonly ILogger<GitService> _logger;
    private readonly SemaphoreSlim _branchSetupLock = new(1, 1);
    private bool? _available;

    public GitService(ILogger<GitService> logger)
    {
        _logger = logger;
    }

    /// <summary>True if git is available on PATH. Checked once on first use.</summary>
    public bool IsAvailable
    {
        get
        {
            if (_available.HasValue) return _available.Value;
            var (ok, _) = RunGitSync(".", "--version");
            _available = ok;
            if (!ok) _logger.LogWarning("Git not found on PATH — git auto-commit disabled");
            return _available.Value;
        }
    }

    public bool IsGitRepo(string workspacePath) =>
        Directory.Exists(Path.Combine(workspacePath, ".git"));

    /// <summary>Cleans up any stale worktrees left by a previous crash. Called on startup.</summary>
    public async Task PruneStaleWorktreesAsync(CancellationToken ct = default)
    {
        // Find any worktree paths in temp that look like ours
        var tmpDir = Path.GetTempPath();
        var stale = Directory.GetDirectories(tmpDir, "sag-ide-wt-*");
        foreach (var wt in stale)
        {
            _logger.LogInformation("Cleaning up stale git worktree: {Path}", wt);
            try { Directory.Delete(wt, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Commits the task result to the specified branch using a temporary git worktree.
    /// The user's working tree is never touched.
    /// </summary>
    public async Task CommitTaskResultAsync(
        string workspacePath,
        string taskId,
        string agentType,
        string description,
        string modelId,
        string output,
        string targetBranch = "sag-agent-log",
        CancellationToken ct = default)
    {
        if (!IsGitRepo(workspacePath))
        {
            _logger.LogDebug("Skipping git commit: {WorkspacePath} is not a git repo", workspacePath);
            return;
        }

        var shortId = taskId[..Math.Min(8, taskId.Length)];
        var worktreePath = Path.Combine(Path.GetTempPath(), $"sag-ide-wt-{shortId}");

        try
        {
            // Ensure the target branch exists (race-safe via lock)
            await EnsureBranchExistsAsync(workspacePath, targetBranch, ct);

            // Add worktree pointing to the agent-log branch
            if (Directory.Exists(worktreePath))
                Directory.Delete(worktreePath, recursive: true);

            var (addOk, addErr) = await RunGitAsync(workspacePath,
                $"worktree add \"{worktreePath}\" {targetBranch}", ct);
            if (!addOk)
            {
                _logger.LogError("Failed to add git worktree: {Error}", addErr);
                return;
            }

            // Write result markdown
            var resultsDir = Path.Combine(worktreePath, ".sag-results");
            Directory.CreateDirectory(resultsDir);
            var mdPath = Path.Combine(resultsDir, $"{taskId}.md");
            await File.WriteAllTextAsync(mdPath, FormatResultMarkdown(taskId, agentType, description, modelId, output), ct);

            // Stage and commit
            await RunGitAsync(worktreePath, "add .sag-results/", ct);

            var safeSummary = description.Length > 60 ? description[..60] + "..." : description;
            var commitMsg = $"sag({agentType}): {safeSummary} [{shortId}]";
            var (commitOk, commitErr) = await RunGitAsync(worktreePath,
                $"commit --no-verify -m \"{commitMsg.Replace("\"", "'")}\"", ct);

            if (commitOk)
                _logger.LogInformation("Task {TaskId} result committed to branch '{Branch}'", taskId, targetBranch);
            else if (!commitErr.Contains("nothing to commit"))
                _logger.LogError("Git commit failed: {Error}", commitErr);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to commit task result {TaskId} to git", taskId);
        }
        finally
        {
            // Always remove the worktree
            if (Directory.Exists(worktreePath))
            {
                await RunGitAsync(workspacePath, $"worktree remove --force \"{worktreePath}\"", ct);
            }
        }
    }

    private async Task EnsureBranchExistsAsync(string workspacePath, string branch, CancellationToken ct)
    {
        await _branchSetupLock.WaitAsync(ct);
        try
        {
            var (exists, _) = await RunGitAsync(workspacePath, $"rev-parse --verify {branch}", ct);
            if (exists) return;

            // Create an orphan branch with an empty initial commit
            // We do this in the main worktree, then immediately return to previous branch
            var (currentBranchOk, currentBranch) = await RunGitAsync(workspacePath, "rev-parse --abbrev-ref HEAD", ct);
            var returnTo = currentBranchOk ? currentBranch.Trim() : "HEAD";

            await RunGitAsync(workspacePath, $"checkout --orphan {branch}", ct);
            await RunGitAsync(workspacePath, "rm -rf --cached .", ct);
            await RunGitAsync(workspacePath,
                $"commit --allow-empty --no-verify -m \"sag: initialize {branch} branch\"", ct);
            await RunGitAsync(workspacePath, $"checkout {returnTo}", ct);

            _logger.LogInformation("Created orphan branch '{Branch}' for agent task results", branch);
        }
        finally
        {
            _branchSetupLock.Release();
        }
    }

    private static string FormatResultMarkdown(
        string taskId, string agentType, string description, string modelId, string output)
    {
        return $"""
            # {agentType} Result

            **Task ID:** `{taskId}`
            **Model:** {modelId}
            **Description:** {description}
            **Timestamp:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC

            ---

            {output}

            ---
            *Generated by SAG IDE*
            """;
    }

    private (bool success, string output) RunGitSync(string workingDirectory, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode == 0, stdout + stderr);
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    private async Task<(bool success, string output)> RunGitAsync(
        string workingDirectory, string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start git process");

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return (process.ExitCode == 0, stdout + stderr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("git {Args} in {Dir} threw: {Msg}", arguments, workingDirectory, ex.Message);
            return (false, ex.Message);
        }
    }
}
