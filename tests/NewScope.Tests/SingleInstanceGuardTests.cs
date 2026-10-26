using NewScope.Services;

namespace NewScope.Tests;

public class SingleInstanceGuardTests
{
    [Fact]
    public void OnlyOneGuardCanOwnTheSameName()
    {
        var name = $"NewScope.Tests.{Guid.NewGuid():N}";

        Assert.True(SingleInstanceGuard.TryAcquire(name, out var first));
        Assert.NotNull(first);
        try
        {
            var secondAcquired = true;
            SingleInstanceGuard? second = null;
            var thread = new Thread(() => secondAcquired = SingleInstanceGuard.TryAcquire(name, out second));
            thread.Start();
            thread.Join();
            Assert.False(secondAcquired);
            Assert.Null(second);
        }
        finally
        {
            first.Dispose();
        }

        Assert.True(SingleInstanceGuard.TryAcquire(name, out var third));
        third?.Dispose();
    }
}
