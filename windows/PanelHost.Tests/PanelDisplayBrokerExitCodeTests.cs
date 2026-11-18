using System.Diagnostics;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Broker;

namespace PanelHost.Tests;

/// <summary>
/// The broker's exit code is the only trace of why it left during start-up: it runs hidden and
/// writes no log. Reading it must give the code, give nothing for a live process, and never throw
/// for a process the cleanup already released. (Probe written by the reviewer.)
/// </summary>
[TestClass]
public sealed class PanelDisplayBrokerExitCodeTests
{
    private static Task<int?> Read(Process? process) =>
        (Task<int?>)typeof(PanelDisplayBrokerClient)
            .GetMethod("TryReadExitCodeAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [process])!;

    private static Process Start(string arguments)
    {
        var process = Process.Start(new ProcessStartInfo("cmd.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        process.EnableRaisingEvents = true;
        return process;
    }

    [TestMethod]
    public async Task An_exited_process_gives_its_exit_code()
    {
        using var process = Start("/c exit 7");

        Assert.AreEqual(7, await Read(process));
    }

    [TestMethod]
    public async Task A_process_that_keeps_running_gives_no_code_after_about_a_second()
    {
        using var process = Start("/c ping -n 8 127.0.0.1 >nul");
        var clock = Stopwatch.StartNew();
        try
        {
            Assert.IsNull(await Read(process));
            // Bounded by the 1 s wait; the wide upper limit only avoids a flaky CI machine.
            Assert.IsTrue(
                clock.ElapsedMilliseconds is > 700 and < 5000,
                clock.ElapsedMilliseconds.ToString());
        }
        finally
        {
            process.Kill();
        }
    }

    [TestMethod]
    public async Task A_process_released_before_or_during_the_wait_does_not_throw()
    {
        var released = Start("/c exit 3");
        released.WaitForExit();
        released.Dispose();
        _ = await Read(released);

        var duringWait = Start("/c ping -n 4 127.0.0.1 >nul");
        var pending = Read(duringWait);
        await Task.Delay(150);
        duringWait.Dispose();
        _ = await pending;
    }

    [TestMethod]
    public async Task No_process_gives_no_code()
    {
        Assert.IsNull(await Read(null));
    }
}
