using RAMSpeed.Services;

namespace RAMSpeed.Tests;

public class MemoryInfoServiceTests
{
    [Fact]
    public void Dispose_is_idempotent()
    {
        var service = new MemoryInfoService();

        service.Dispose();
        service.Dispose();
    }

    [Fact]
    public void GetCurrentMemoryInfo_throws_after_dispose()
    {
        var service = new MemoryInfoService();
        service.Dispose();

        Assert.Throws<ObjectDisposedException>(() => service.GetCurrentMemoryInfo());
    }
}
