using SAGIDE.Core.Models;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Tests;

public class AgentLimitsConfigTests
{
    [Fact]
    public void GetMaxIterations_KnownAgent_ReturnsConfigured()
    {
        var config = new AgentLimitsConfig();
        Assert.Equal(5, config.GetMaxIterations(AgentType.Refactoring));
    }

    [Fact]
    public void GetMaxIterations_UnknownAgent_ReturnsDefault()
    {
        var config = new AgentLimitsConfig();
        // Default fallback is 5
        Assert.Equal(5, config.GetMaxIterations((AgentType)999));
    }
}
