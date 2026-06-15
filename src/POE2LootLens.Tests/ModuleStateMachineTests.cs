using Poe2LootLens;

namespace Poe2LootLens.Tests;

public class ModuleStateMachineTests
{
    [Fact]
    public void StartAndStop_FollowExpectedLifecycle()
    {
        var machine = new ModuleStateMachine();

        Assert.Equal(ModuleState.Stopped, machine.Snapshot.State);
        Assert.True(machine.TryBeginStart());
        Assert.Equal(ModuleState.Starting, machine.Snapshot.State);

        machine.MarkRunning();
        Assert.True(machine.Snapshot.IsRunning);
        Assert.True(machine.TryBeginStop());
        Assert.True(machine.Snapshot.IsBusy);

        machine.MarkStopped();
        Assert.Equal(ModuleState.Stopped, machine.Snapshot.State);
        Assert.Null(machine.Snapshot.LastError);
    }

    [Fact]
    public void ConcurrentLifecycleRequests_AreRejected()
    {
        var machine = new ModuleStateMachine();

        Assert.True(machine.TryBeginStart());
        Assert.False(machine.TryBeginStart());
        Assert.False(machine.TryBeginStop());

        machine.MarkRunning();
        Assert.False(machine.TryBeginStart());
        Assert.True(machine.TryBeginStop());
        Assert.False(machine.TryBeginStop());
    }

    [Fact]
    public void FaultedModule_CanBeRetriedAndClearsPreviousError()
    {
        var machine = new ModuleStateMachine();
        Assert.True(machine.TryBeginStart());

        machine.MarkFaulted(new InvalidOperationException("OCR model missing"));

        Assert.Equal(ModuleState.Faulted, machine.Snapshot.State);
        Assert.Contains("OCR model missing", machine.Snapshot.LastError ?? string.Empty);
        Assert.True(machine.TryBeginStart());
        Assert.Equal(ModuleState.Starting, machine.Snapshot.State);
        Assert.Null(machine.Snapshot.LastError);
    }

    [Fact]
    public void InvalidCompletionTransition_Throws()
    {
        var machine = new ModuleStateMachine();

        Assert.Throws<InvalidOperationException>(machine.MarkRunning);
        Assert.Throws<InvalidOperationException>(machine.MarkStopped);
    }
}
