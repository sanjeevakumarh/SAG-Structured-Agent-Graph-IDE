using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using SAGIDE.Core.Interfaces;
using SAGIDE.Service.Agents;
using SAGIDE.Service.Communication;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Service.Persistence;
using SAGIDE.Service.Providers;
using SAGIDE.Service.Resilience;
using SAGIDE.Service.ActivityLogging;
using SAGIDE.Service.Infrastructure;

using ServiceLifetimeHosted = SAGIDE.Service.Services.ServiceLifetime;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/agentic-ide-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    Log.Information("Starting Agentic IDE Service");

    var builder = Host.CreateApplicationBuilder(args);

    // Configure Serilog
    builder.Services.AddSerilog();

    // Read config
    var pipeName      = builder.Configuration["SAGIDE:NamedPipeName"] ?? "SAGIDEPipe";
    var maxConcurrent = builder.Configuration.GetValue("SAGIDE:MaxConcurrentAgents", 5);

    // Bind resilience configs from appsettings
    var timeoutConfig = new TimeoutConfig();
    builder.Configuration.GetSection("SAGIDE:Timeouts").Bind(timeoutConfig);
    builder.Services.AddSingleton(timeoutConfig);

    var agentLimitsConfig = new AgentLimitsConfig();
    builder.Configuration.GetSection("SAGIDE:AgentLimits").Bind(agentLimitsConfig);
    builder.Services.AddSingleton(agentLimitsConfig);

    // Bind TaskAffinities from appsettings (Item 6 — Smart Router fallback)
    var taskAffinitiesConfig = new TaskAffinitiesConfig();
    builder.Configuration.GetSection("SAGIDE:TaskAffinities").Bind(taskAffinitiesConfig.Affinities);
    builder.Services.AddSingleton(taskAffinitiesConfig);

    // Bind WorkflowPolicy from appsettings (Item 3 — Policy Engine)
    var workflowPolicyConfig = new WorkflowPolicyConfig();
    builder.Configuration.GetSection("SAGIDE:WorkflowPolicy").Bind(workflowPolicyConfig);
    builder.Services.AddSingleton(workflowPolicyConfig);
    builder.Services.AddSingleton<WorkflowPolicyEngine>();

    // Register SQLite persistence
    var dbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SAGIDE", "agentic-ide.db");
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

    builder.Services.AddSingleton<ITaskRepository>(sp =>
    {
        var repo = new SqliteTaskRepository(dbPath, sp.GetRequiredService<ILogger<SqliteTaskRepository>>());
        repo.InitializeAsync().GetAwaiter().GetResult();
        return repo;
    });

    // Wire up the additional interfaces that SqliteTaskRepository implements (same instance)
    builder.Services.AddSingleton<IActivityRepository>(sp =>
        (IActivityRepository)sp.GetRequiredService<ITaskRepository>());
    builder.Services.AddSingleton<IWorkflowRepository>(sp =>
        (IWorkflowRepository)sp.GetRequiredService<ITaskRepository>());

    // Register activity logging services
    builder.Services.AddSingleton<MarkdownGenerator>();
    builder.Services.AddSingleton<ActivityLogger>();
    builder.Services.AddSingleton<GitIntegration>();

    // Register git auto-commit service
    var gitConfig = new GitConfig();
    builder.Configuration.GetSection("SAGIDE:Git").Bind(gitConfig);
    builder.Services.AddSingleton(gitConfig);
    builder.Services.AddSingleton<GitService>();

    // Register dead-letter queue (with persistence)
    builder.Services.AddSingleton(sp => new DeadLetterQueue(
        sp.GetRequiredService<ILogger<DeadLetterQueue>>(),
        sp.GetRequiredService<ITaskRepository>()));

    // Register result parser
    builder.Services.AddSingleton<ResultParser>();

    // Register providers via factory (with resilience)
    var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddSerilog());
    var providerFactory = new ProviderFactory(builder.Configuration, loggerFactory, timeoutConfig);

    foreach (var provider in providerFactory.GetAllProviders())
        builder.Services.AddSingleton(typeof(IAgentProvider), provider);

    builder.Services.AddSingleton(providerFactory);

    // Register orchestrator with all dependencies
    builder.Services.AddSingleton<TaskQueue>();
    builder.Services.AddSingleton(sp => new AgentOrchestrator(
        sp.GetRequiredService<TaskQueue>(),
        sp,
        sp.GetRequiredService<DeadLetterQueue>(),
        sp.GetRequiredService<TimeoutConfig>(),
        sp.GetRequiredService<AgentLimitsConfig>(),
        sp.GetRequiredService<ResultParser>(),
        sp.GetRequiredService<ILogger<AgentOrchestrator>>(),
        sp.GetRequiredService<ITaskRepository>(),
        sp.GetRequiredService<ActivityLogger>(),
        maxConcurrent,
        sp.GetRequiredService<GitService>(),
        sp.GetRequiredService<GitConfig>()));

    // Register workflow engine with all dependencies (Items 1, 3, 4, 6)
    builder.Services.AddSingleton<WorkflowDefinitionLoader>();
    builder.Services.AddSingleton(sp => new WorkflowEngine(
        sp.GetRequiredService<AgentOrchestrator>(),
        sp.GetRequiredService<WorkflowDefinitionLoader>(),
        sp.GetRequiredService<AgentLimitsConfig>(),
        sp.GetRequiredService<TaskAffinitiesConfig>(),
        sp.GetRequiredService<WorkflowPolicyEngine>(),
        sp.GetRequiredService<ILogger<WorkflowEngine>>(),
        sp.GetService<IWorkflowRepository>()));

    // Register communication
    builder.Services.AddSingleton<MessageHandler>();
    builder.Services.AddSingleton(sp => new NamedPipeServer(
        pipeName,
        sp.GetRequiredService<MessageHandler>(),
        sp.GetRequiredService<ILogger<NamedPipeServer>>()));

    // Register hosted service
    builder.Services.AddHostedService<ServiceLifetimeHosted>();

    var host = builder.Build();

    // Wire WorkflowEngine back into AgentOrchestrator (post-construction to break circular dep)
    host.Services.GetRequiredService<AgentOrchestrator>()
        .SetWorkflowEngine(host.Services.GetRequiredService<WorkflowEngine>());

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application start-up failed");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;
