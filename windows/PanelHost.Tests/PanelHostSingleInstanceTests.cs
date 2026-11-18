using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PanelHost.Tests;

[TestClass]
public sealed class PanelHostSingleInstanceTests
{
    [TestMethod]
    public async Task Second_host_is_detected_without_exception_and_name_is_reusable()
    {
        var name = $"Local\\PanelOS.PanelHost.Tests.{Guid.NewGuid():N}";
        using (var first = PanelHostSingleInstance.TryAcquire(name))
        {
            Assert.IsTrue(first.IsOwner);
            var secondOwned = await Task.Run(() =>
            {
                using var second = PanelHostSingleInstance.TryAcquire(name);
                return second.IsOwner;
            });
            Assert.IsFalse(secondOwned);
        }

        using var afterExit = PanelHostSingleInstance.TryAcquire(name);
        Assert.IsTrue(afterExit.IsOwner);
        Assert.AreEqual(17, PanelHostSingleInstance.AlreadyRunningExitCode);
    }
}
