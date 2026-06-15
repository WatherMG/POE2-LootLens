namespace Poe2LootLens;

internal enum ModuleState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted,
}

internal readonly record struct ModuleStateSnapshot(
    ModuleState State,
    string? LastError,
    DateTime ChangedAtUtc)
{
    public bool IsBusy => State is ModuleState.Starting or ModuleState.Stopping;
    public bool IsRunning => State == ModuleState.Running;
}

/// <summary>
/// Small, explicit lifecycle state machine shared by both background modules.
/// The scanner instances still own their OCR loops; this class owns only UI-visible lifecycle
/// transitions and prevents overlapping start/stop operations.
/// </summary>
internal sealed class ModuleStateMachine
{
    private readonly object _sync = new();
    private ModuleState _state = ModuleState.Stopped;
    private string? _lastError;
    private DateTime _changedAtUtc = DateTime.UtcNow;

    public ModuleStateSnapshot Snapshot
    {
        get
        {
            lock (_sync)
                return new ModuleStateSnapshot(_state, _lastError, _changedAtUtc);
        }
    }

    public bool TryBeginStart()
    {
        lock (_sync)
        {
            if (_state is not (ModuleState.Stopped or ModuleState.Faulted))
                return false;

            SetState(ModuleState.Starting, error: null);
            return true;
        }
    }

    public void MarkRunning()
    {
        lock (_sync)
        {
            EnsureState(ModuleState.Starting);
            SetState(ModuleState.Running, error: null);
        }
    }

    public bool TryBeginStop()
    {
        lock (_sync)
        {
            if (_state != ModuleState.Running)
                return false;

            SetState(ModuleState.Stopping, error: null);
            return true;
        }
    }

    public void MarkStopped()
    {
        lock (_sync)
        {
            EnsureState(ModuleState.Stopping);
            SetState(ModuleState.Stopped, error: null);
        }
    }

    public void MarkFaulted(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_sync)
            SetState(ModuleState.Faulted, $"{exception.GetType().Name}: {exception.Message}");
    }

    private void EnsureState(ModuleState expected)
    {
        if (_state != expected)
        {
            throw new InvalidOperationException(
                $"Invalid module state transition: expected {expected}, actual {_state}.");
        }
    }

    private void SetState(ModuleState state, string? error)
    {
        _state = state;
        _lastError = error;
        _changedAtUtc = DateTime.UtcNow;
    }
}
