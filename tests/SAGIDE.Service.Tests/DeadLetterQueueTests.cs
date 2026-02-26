using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Models;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Tests;

public class DeadLetterQueueTests
{
    private static DeadLetterQueue CreateDlq() =>
        new(NullLogger<DeadLetterQueue>.Instance);

    [Fact]
    public void Enqueue_IncreasesCount()
    {
        var dlq = CreateDlq();
        var task = new AgentTask { Description = "Test", AgentType = AgentType.Debug };

        dlq.Enqueue(task, "Some error");

        Assert.Equal(1, dlq.Count);
    }

    [Fact]
    public void DequeueForRetry_RemovesEntry()
    {
        var dlq = CreateDlq();
        var task = new AgentTask { Description = "Test" };
        dlq.Enqueue(task, "err");

        var entries = dlq.GetAll();
        var entry = dlq.DequeueForRetry(entries[0].Id);

        Assert.NotNull(entry);
        Assert.Equal(0, dlq.Count);
    }

    [Fact]
    public void Discard_RemovesEntry()
    {
        var dlq = CreateDlq();
        var task = new AgentTask { Description = "Test" };
        dlq.Enqueue(task, "err");

        var entries = dlq.GetAll();
        Assert.True(dlq.Discard(entries[0].Id));
        Assert.Equal(0, dlq.Count);
    }

    [Fact]
    public void DequeueForRetry_NonExistent_ReturnsNull()
    {
        var dlq = CreateDlq();
        Assert.Null(dlq.DequeueForRetry("doesnotexist"));
    }
}
