using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Service.Persistence;

namespace SAGIDE.Service.Infrastructure;

/// <summary>
/// Runs async database initialization (schema bootstrap, sample pruning) as a hosted service
/// so startup never blocks a thread-pool thread with GetAwaiter().GetResult().
/// Registered inside <see cref="ServiceCollectionExtensions.AddSagidePersistence"/> — before
/// other hosted services — to guarantee tables exist before first use.
/// </summary>
public sealed class DatabaseInitializer : IHostedService
{
    private readonly ITaskRepository _taskRepo;
    private readonly IModelPerfRepository _perfRepo;
    private readonly IModelQualityRepository _qualityRepo;
    private readonly NotesFileIndexRepository _notesFileIndex;
    private readonly SearchCacheRepository _searchCache;
    private readonly IConfiguration _config;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        ITaskRepository taskRepo,
        IModelPerfRepository perfRepo,
        IModelQualityRepository qualityRepo,
        NotesFileIndexRepository notesFileIndex,
        SearchCacheRepository searchCache,
        IConfiguration config,
        ILogger<DatabaseInitializer> logger)
    {
        _taskRepo       = taskRepo;
        _perfRepo       = perfRepo;
        _qualityRepo    = qualityRepo;
        _notesFileIndex = notesFileIndex;
        _searchCache    = searchCache;
        _config         = config;
        _logger         = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing databases...");

        // Schema bootstrap — creates all tables (task_history, results, workflows, etc.)
        await _taskRepo.InitializeAsync();
        await _notesFileIndex.InitializeAsync();
        await _searchCache.InitializeAsync();

        // Prune old model performance/quality samples
        var perfRetention    = _config.GetValue("SAGIDE:Routing:PerfRetentionDays", 3);
        var qualityRetention = _config.GetValue("SAGIDE:Routing:QualityRetentionDays", 7);
        await _perfRepo.PruneOldSamplesAsync(perfRetention);
        await _qualityRepo.PruneOldSamplesAsync(qualityRetention);

        // Prune old search cache entries
        var searchRetention = _config.GetValue("SAGIDE:Caching:SearchCacheRetentionDays", 30);
        await _searchCache.PruneAsync(searchRetention);

        _logger.LogInformation("Database initialization complete");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
