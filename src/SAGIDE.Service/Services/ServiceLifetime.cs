using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SAGIDE.Service.Communication;
using SAGIDE.Service.Communication.Messages;
using SAGIDE.Service.Orchestrator;

namespace SAGIDE.Service.Services;

public class ServiceLifetime : BackgroundService
{
    private readonly NamedPipeServer _pipeServer;
    private readonly AgentOrchestrator _orchestrator;
    private readonly WorkflowEngine _workflowEngine;
    private readonly ILogger<ServiceLifetime> _logger;

    public ServiceLifetime(
        NamedPipeServer pipeServer,
        AgentOrchestrator orchestrator,
        WorkflowEngine workflowEngine,
        ILogger<ServiceLifetime> logger)
    {
        _pipeServer    = pipeServer;
        _orchestrator  = orchestrator;
        _workflowEngine = workflowEngine;
        _logger        = logger;

        // Broadcast every task-status change to all connected VSCode clients.
        _orchestrator.OnTaskUpdate += status =>
        {
            var msg = new PipeMessage
            {
                Type = MessageTypes.TaskUpdate,
                Payload = JsonSerializer.SerializeToUtf8Bytes(status, NamedPipeServer.JsonOptions),
            };
            _ = _pipeServer.BroadcastAsync(msg);
        };

        // Broadcast streaming output chunks (throttled ~200ms in orchestrator).
        _orchestrator.OnStreamingOutput += streamMsg =>
        {
            var msg = new PipeMessage
            {
                Type = MessageTypes.StreamingOutput,
                Payload = JsonSerializer.SerializeToUtf8Bytes(streamMsg, NamedPipeServer.JsonOptions),
            };
            _ = _pipeServer.BroadcastAsync(msg);
        };

        // Broadcast workflow instance updates (step completions, status changes).
        _workflowEngine.OnWorkflowUpdate += instance =>
        {
            var msg = new PipeMessage
            {
                Type = MessageTypes.WorkflowUpdate,
                Payload = JsonSerializer.SerializeToUtf8Bytes(instance, NamedPipeServer.JsonOptions),
            };
            _ = _pipeServer.BroadcastAsync(msg);
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Agentic IDE Service starting...");

        // Start pipe server and orchestrator in parallel
        var pipeTask        = _pipeServer.StartAsync(stoppingToken);
        var orchestratorTask = _orchestrator.StartProcessingAsync(stoppingToken);

        // Wait for the orchestrator to finish loading persisted tasks before recovering
        // workflow instances.  Recovery resubmits steps to the task queue; if the queue
        // is not yet populated the _taskToStep reverse map will be stale after restart.
        await _orchestrator.InitializationCompleted;
        _ = _workflowEngine.RecoverRunningInstancesAsync(stoppingToken);

        _logger.LogInformation("Agentic IDE Service is ready");

        await Task.WhenAll(pipeTask, orchestratorTask);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Agentic IDE Service stopping...");
        await _pipeServer.StopAsync();
        await base.StopAsync(cancellationToken);
    }
}
