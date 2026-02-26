using SAGIDE.Core.Models;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Tests;

public class TimeoutConfigTests
{
    [Fact]
    public void GetProviderTimeoutMs_CloudProviders_Return300Seconds()
    {
        var config = new TimeoutConfig();
        Assert.Equal(300_000, config.GetProviderTimeoutMs(ModelProvider.Claude));
        Assert.Equal(300_000, config.GetProviderTimeoutMs(ModelProvider.Codex));
        Assert.Equal(300_000, config.GetProviderTimeoutMs(ModelProvider.Gemini));
    }

    [Fact]
    public void GetProviderTimeoutMs_Ollama_Returns1800Seconds()
    {
        var config = new TimeoutConfig();
        Assert.Equal(1_800_000, config.GetProviderTimeoutMs(ModelProvider.Ollama));
    }

    [Fact]
    public void TaskExecutionTimeout_ReturnsTimeSpan()
    {
        var config = new TimeoutConfig { TaskExecutionMs = 60_000 };
        Assert.Equal(TimeSpan.FromMinutes(1), config.TaskExecutionTimeout);
    }
}
