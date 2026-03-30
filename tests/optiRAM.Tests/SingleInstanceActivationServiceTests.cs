using System.Threading;
using optiRAM.Services;

namespace optiRAM.Tests;

public class SingleInstanceActivationServiceTests
{
    [Fact]
    public void SignalExistingInstance_returns_false_when_listener_missing()
    {
        var eventName = $"optiRAM.Tests.Missing.{Guid.NewGuid():N}";

        var sent = SingleInstanceActivationService.SignalExistingInstance(eventName);

        Assert.False(sent);
    }

    [Fact]
    public void SignalExistingInstance_triggers_registered_callback()
    {
        var eventName = $"optiRAM.Tests.Activation.{Guid.NewGuid():N}";
        using var signaled = new ManualResetEventSlim();
        using var service = new SingleInstanceActivationService(eventName, () => signaled.Set());

        var sent = SingleInstanceActivationService.SignalExistingInstance(eventName);

        Assert.True(sent);
        Assert.True(signaled.Wait(TimeSpan.FromSeconds(2)));
    }
}
