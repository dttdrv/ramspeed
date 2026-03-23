using System.Threading;

namespace RAMSpeed.Services;

internal sealed class SingleInstanceActivationService : IDisposable
{
    private readonly EventWaitHandle _activationEvent;
    private readonly RegisteredWaitHandle _registeredWait;

    public SingleInstanceActivationService(string eventName, Action onActivated)
    {
        ArgumentNullException.ThrowIfNull(eventName);
        ArgumentNullException.ThrowIfNull(onActivated);

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            static (state, _) => ((Action)state!).Invoke(),
            onActivated,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public static bool SignalExistingInstance(string eventName)
    {
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(eventName);
            return activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _registeredWait.Unregister(null);
        _activationEvent.Dispose();
    }
}
