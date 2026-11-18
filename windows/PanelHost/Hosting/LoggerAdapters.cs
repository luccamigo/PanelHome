using System.Text;
using Microsoft.Extensions.Logging;
using PanelOS.Windows.StreamDeck.Runtime;

namespace PanelHost.Hosting;

/// <summary>Compatibility bridge for feature code whose contract still accepts a TextWriter.</summary>
internal sealed class LoggerTextWriter(ILogger logger, LogLevel level = LogLevel.Information)
    : TextWriter
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override Encoding Encoding => Encoding.UTF8;

    public override void WriteLine(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _logger.Log(level, "{Message}", value);
        }
    }

    public override void Write(char value)
    {
        if (value != '\r' && value != '\n')
        {
            _logger.Log(level, "{Message}", value.ToString());
        }
    }
}

internal sealed class LoggerStreamDeckLog(ILogger<LoggerStreamDeckLog> logger)
    : IStreamDeckLog
{
    public void Write(string message) => logger.LogInformation("{Message}", message);
}
