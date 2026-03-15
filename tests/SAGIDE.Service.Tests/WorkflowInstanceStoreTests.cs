using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Models;
using SAGIDE.Service.Events;
using SAGIDE.Service.Infrastructure;
using SAGIDE.Service.Orchestrator;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Unit tests for <see cref="WorkflowInstanceStore"/> covering:
/// - TryGet returns false and null out-params when instance is missing
/// - TryGet returns true and correct values when instance is present
/// - Add registers instance, def, lock, and reverse-dep cache
/// - BuildReverseDeps produces correct adjacency map
/// </summary>
public class WorkflowInstanceStoreTests
{
    private static WorkflowInstanceStore MakeStore() => new(
        repository: null,
        gitService: new GitService(NullLogger<GitService>.Instance),
        eventBus:   new NullEventBus(),
        logger:     NullLogger<WorkflowInstanceStore>.Instance);

    private static (WorkflowInstance inst, WorkflowDefinition def) MakePair(string id = "inst-1")
    {
        var inst = new WorkflowInstance { InstanceId = id };
        var def  = new WorkflowDefinition { Id = "wf-1", Name = "Test" };
        return (inst, def);
    }

    // ── TryGet — missing instance ─────────────────────────────────────────────

    [Fact]
    public void TryGet_MissingInstance_ReturnsFalseAndNullOutParams()
    {
        var store = MakeStore();

        var found = store.TryGet("nonexistent", out var inst, out var def);

        Assert.False(found);
        Assert.Null(inst);
        Assert.Null(def);
    }

    // ── TryGet — present instance ─────────────────────────────────────────────

    [Fact]
    public void TryGet_AfterAdd_ReturnsTrueAndCorrectValues()
    {
        var store = MakeStore();
        var (inst, def) = MakePair("abc");
        store.Add(inst, def);

        var found = store.TryGet("abc", out var gotInst, out var gotDef);

        Assert.True(found);
        Assert.Same(inst, gotInst);
        Assert.Same(def,  gotDef);
    }

    // ── Add — populates all four maps ─────────────────────────────────────────

    [Fact]
    public void Add_PopulatesActiveLocksAndRevDepsCache()
    {
        var store = MakeStore();
        var (inst, def) = MakePair("x1");

        store.Add(inst, def);

        Assert.Equal(1, store.Count);
        Assert.True(store.Active.ContainsKey("x1"));
        Assert.True(store.Locks.ContainsKey("x1"));
        Assert.True(store.RevDepsCache.ContainsKey("x1"));
    }

    // ── Count reflects active instances ──────────────────────────────────────

    [Fact]
    public void Count_ReflectsNumberOfActiveInstances()
    {
        var store = MakeStore();
        Assert.Equal(0, store.Count);

        var (inst1, def1) = MakePair("i1");
        var (inst2, def2) = MakePair("i2");
        store.Add(inst1, def1);
        store.Add(inst2, def2);

        Assert.Equal(2, store.Count);
    }

    // ── GetLock — returns the semaphore added for the instance ────────────────

    [Fact]
    public void GetLock_AfterAdd_ReturnsSemaphore()
    {
        var store = MakeStore();
        var (inst, def) = MakePair("lock-test");
        store.Add(inst, def);

        var sem = store.GetLock("lock-test");

        Assert.NotNull(sem);
        Assert.Equal(1, sem.CurrentCount); // starts released
    }

    // ── BuildReverseDeps ──────────────────────────────────────────────────────

    [Fact]
    public void BuildReverseDeps_NoDependencies_ReturnsEmptyMap()
    {
        var def = new WorkflowDefinition
        {
            Steps =
            [
                new WorkflowStepDef { Id = "a" },
                new WorkflowStepDef { Id = "b" },
            ]
        };

        var rev = WorkflowInstanceStore.BuildReverseDeps(def);

        Assert.Empty(rev);
    }

    [Fact]
    public void BuildReverseDeps_LinearChain_MapsCorrectly()
    {
        // a → b → c
        var def = new WorkflowDefinition
        {
            Steps =
            [
                new WorkflowStepDef { Id = "a" },
                new WorkflowStepDef { Id = "b", DependsOn = ["a"] },
                new WorkflowStepDef { Id = "c", DependsOn = ["b"] },
            ]
        };

        var rev = WorkflowInstanceStore.BuildReverseDeps(def);

        // "a" is a dep of "b", so rev["a"] = ["b"]
        Assert.True(rev.TryGetValue("a", out var aDownstream));
        Assert.Equal(["b"], aDownstream);

        // "b" is a dep of "c", so rev["b"] = ["c"]
        Assert.True(rev.TryGetValue("b", out var bDownstream));
        Assert.Equal(["c"], bDownstream);

        // "c" is not a dep of anything
        Assert.False(rev.ContainsKey("c"));
    }

    [Fact]
    public void BuildReverseDeps_FanIn_CollectsAllDependents()
    {
        // a and b both depend on root → rev["root"] = ["a", "b"]
        var def = new WorkflowDefinition
        {
            Steps =
            [
                new WorkflowStepDef { Id = "root" },
                new WorkflowStepDef { Id = "a", DependsOn = ["root"] },
                new WorkflowStepDef { Id = "b", DependsOn = ["root"] },
            ]
        };

        var rev = WorkflowInstanceStore.BuildReverseDeps(def);

        Assert.True(rev.TryGetValue("root", out var downstream));
        Assert.Equal(2, downstream!.Count);
        Assert.Contains("a", downstream);
        Assert.Contains("b", downstream);
    }

    [Fact]
    public void BuildReverseDeps_FanOut_EachDepHasSingleEntry()
    {
        // c depends on both a and b
        var def = new WorkflowDefinition
        {
            Steps =
            [
                new WorkflowStepDef { Id = "a" },
                new WorkflowStepDef { Id = "b" },
                new WorkflowStepDef { Id = "c", DependsOn = ["a", "b"] },
            ]
        };

        var rev = WorkflowInstanceStore.BuildReverseDeps(def);

        Assert.True(rev.TryGetValue("a", out var aDown));
        Assert.Equal(["c"], aDown);

        Assert.True(rev.TryGetValue("b", out var bDown));
        Assert.Equal(["c"], bDown);
    }
}
