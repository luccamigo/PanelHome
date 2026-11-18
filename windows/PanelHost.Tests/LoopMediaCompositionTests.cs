using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Hosting;
using PanelHost.Server;
using PanelHost.Services;
using PanelOS.Windows.LoopMedia;

namespace PanelHost.Tests;

[TestClass]
public sealed class LoopMediaCompositionTests
{
    // Incorporates an independent review's ZzLoopMediaCompositionProbe against the real registration graph.
    [TestMethod]
    public void Production_graph_gives_the_session_the_same_loop_media_service()
    {
        using var host = PanelHostApplication.BuildHost(Options());
        var session = host.Services.GetRequiredService<PanelClientSession>();
        var service = host.Services.GetRequiredService<LoopMediaService>();
        Assert.AreSame(service, Field<LoopMediaService?>(session, "_loopMediaService"));
    }

    [TestMethod]
    public async Task Hosted_start_loads_the_published_media_before_starting_listeners()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PanelHost.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "sample.gif");
            await File.WriteAllBytesAsync(source, "GIF89a"u8.ToArray());
            var store = new JsonLoopMediaStore(Path.Combine(directory, "media.json"), Path.Combine(directory, "media"));
            var media = await store.ImportAsync(source, CancellationToken.None);
            await using var service = new LoopMediaService(store);
            await using var deck = StreamDeckSessionTests.StreamDeckHarness.CreateRuntime();
            // Test-owned provider and executor; never read or watch the operator's Stream Deck configuration.
            var wrapper = (PanelHostStreamDeck)RuntimeHelpers.GetUninitializedObject(typeof(PanelHostStreamDeck));
            SetField(wrapper, "<Provider>k__BackingField", deck.Provider);
            SetField(wrapper, "<Service>k__BackingField", deck.Service);

            using var host = PanelHostApplication.BuildHost(Options());
            var runtime = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                .OfType<PanelHostRuntimeHostedService>().Single();
            SetField(runtime, "_telemetry", new FakeTelemetryService());
            SetField(runtime, "_media", new FakeMediaService());
            SetField(runtime, "_volume", new FakeVolumeService());
            SetField(runtime, "_streamDeck", wrapper);
            SetField(runtime, "_loopMedia", service);
            SetField(runtime, "_logger", new StopBeforeListenersLogger());

            // Exercise real startup through the initialization boundary, stopping before hardware/listeners.
            var run = typeof(PanelHostRuntimeHostedService).GetMethod("RunAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var task = (Task)run.Invoke(runtime, [CancellationToken.None])!;
            await Assert.ThrowsExceptionAsync<StartupBoundaryReached>(() => task);
            Assert.IsTrue(service.GetState().Available);
            Assert.AreEqual(media.MediaId, service.GetState().MediaId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static PanelHostOptions Options() => new()
    {
        Transport = PanelHostTransportKind.Usb,
        FakeDesktop = true,
        VideoTestPattern = true,
    };

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static void SetField(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private sealed class StartupBoundaryReached : Exception;

    private sealed class StopBeforeListenersLogger : ILogger<PanelHostRuntimeHostedService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).StartsWith("Transport ", StringComparison.Ordinal))
                throw new StartupBoundaryReached();
        }
    }
}
