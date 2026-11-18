using PanelHost.Services;
using PanelOS.Windows;
using PanelOS.Windows.StreamDeck;
using PanelOS.Windows.StreamDeck.Icons;
using PanelOS.Windows.StreamDeck.Runtime;

namespace PanelHost.Hosting;

/// <summary>Keeps the provider's ownership with StreamDeckService.</summary>
internal sealed class PanelHostStreamDeck : IAsyncDisposable
{
    internal PanelHostStreamDeck(LoggerStreamDeckLog log)
    {
        var store = new JsonStreamDeckConfigurationStore(
            PanelOSPaths.StreamDeckConfigurationFile,
            quarantineInvalidFile: false);
        Provider = new StreamDeckConfigurationProvider(
            store,
            new StreamDeckConfigurationFileWatcher(
                PanelOSPaths.StreamDeckConfigurationFile),
            log);
        Service = new StreamDeckService(
            Provider,
            new WindowsStreamDeckActionExecutor(
                store,
                new SendInputKeyboardService(),
                new WindowsProcessLauncher(),
                log: log),
            new StreamDeckIconCache(),
            log);
    }

    internal StreamDeckConfigurationProvider Provider { get; }

    internal StreamDeckService Service { get; }

    public ValueTask DisposeAsync() => Service.DisposeAsync();
}
