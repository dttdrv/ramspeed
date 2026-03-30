using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace optiRAM.Services;

internal sealed class SingleInstanceActivationService : IDisposable
{
    private readonly EventWaitHandle _activationEvent;
    private readonly RegisteredWaitHandle _registeredWait;

    public SingleInstanceActivationService(string eventName, Action onActivated)
    {
        ArgumentNullException.ThrowIfNull(eventName);
        ArgumentNullException.ThrowIfNull(onActivated);

        // Create with explicit security so both elevated and non-elevated processes
        // can open and signal this event (cross-integrity-level communication).
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify,
            AccessControlType.Allow));
        security.AddAccessRule(new EventWaitHandleAccessRule(
            WindowsIdentity.GetCurrent().User!,
            EventWaitHandleRights.FullControl,
            AccessControlType.Allow));

        _activationEvent = EventWaitHandleAcl.Create(
            false, EventResetMode.AutoReset, eventName, out _, security);
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
        catch (UnauthorizedAccessException)
        {
            // Cross-integrity-level access denied (e.g., non-elevated trying to signal elevated)
            return false;
        }
    }

    public void Dispose()
    {
        _registeredWait.Unregister(null);
        _activationEvent.Dispose();
    }
}
