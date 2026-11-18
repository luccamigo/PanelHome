using System.Diagnostics;
using System.Text;

namespace PanelOS.Windows.Development;

public sealed record ProcessRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs a command line tool and captures its output. Abstracted so the ADB
/// flow is testable on a machine that has no Android SDK.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public sealed class ProcessRunner : IProcessRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<ProcessRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,

            // Arguments are passed as a structured list, never as one string
            // for a shell to split: adb is executed directly.
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"O Windows não retornou um processo para {executablePath}.");
        using var timeout = new CancellationTokenSource(Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token,
            cancellationToken);

        var standardOutput = process.StandardOutput.ReadToEndAsync(linked.Token);
        var standardError = process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException(
                $"{Path.GetFileName(executablePath)} não respondeu em " +
                $"{Timeout.TotalSeconds:0} segundos.");
        }

        return new ProcessRunResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }
}

/// <summary>
/// Development-only USB transport: finds adb, reports the connected device and
/// sets up the reverse tunnels PanelHome needs.
/// </summary>
public sealed class AdbDevelopmentTransport : IAdbDevelopmentTransport
{
    // PanelHost listens on 127.0.0.1:8765 (control) and :8766 (video). The
    // numbers are repeated here on purpose: this project cannot reference
    // PanelHost, and the protocol is not modified by the Control Center.
    private static readonly int[] TransportPorts = [8765, 8766];

    private readonly IAdbLocator _locator;
    private readonly IProcessRunner _runner;
    private readonly DevelopmentSessionToken _sessionToken;

    public AdbDevelopmentTransport(
        IAdbLocator locator,
        IProcessRunner? runner = null,
        DevelopmentSessionToken? sessionToken = null)
    {
        ArgumentNullException.ThrowIfNull(locator);
        _locator = locator;
        _runner = runner ?? new ProcessRunner();
        _sessionToken = sessionToken ?? new DevelopmentSessionToken();
    }

    public IReadOnlyList<int> Ports => TransportPorts;

    public async Task<AdbStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        var adbPath = _locator.Locate();
        if (adbPath is null)
        {
            return AdbStatus.NotFound(
                "adb.exe não encontrado. Instale o Android SDK Platform-Tools " +
                "ou informe o caminho em Configurações.");
        }

        ProcessRunResult result;
        try
        {
            result = await _runner
                .RunAsync(adbPath, ["devices"], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AdbStatus
            {
                AdbFound = true,
                AdbPath = adbPath,
                Detail = $"Falha ao executar adb devices: {exception.Message}",
            };
        }

        if (!result.Succeeded)
        {
            return new AdbStatus
            {
                AdbFound = true,
                AdbPath = adbPath,
                Detail = $"adb devices falhou: {Describe(result)}",
            };
        }

        return Interpret(adbPath, ParseDevices(result.StandardOutput));
    }

    public async Task<AdbReverseResult> ConfigureReverseAsync(
        CancellationToken cancellationToken)
    {
        var status = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!status.IsReady || status.AdbPath is null || status.DeviceSerial is null)
        {
            return new AdbReverseResult(
                false,
                status.Detail ?? "Nenhum dispositivo USB pronto.",
                TransportPorts);
        }

        var token = _sessionToken.Rotate();

        foreach (var port in TransportPorts)
        {
            ProcessRunResult result;
            try
            {
                result = await _runner.RunAsync(
                        status.AdbPath,
                        [
                            "-s",
                            status.DeviceSerial,
                            "reverse",
                            $"tcp:{port}",
                            $"tcp:{port}",
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                var detail = _sessionToken.Redact(exception.Message);
                _sessionToken.Clear();
                return new AdbReverseResult(
                    false,
                    $"Falha ao configurar tcp:{port}: {detail}",
                    TransportPorts);
            }

            if (!result.Succeeded)
            {
                var detail = Describe(result);
                detail = _sessionToken.Redact(detail);
                _sessionToken.Clear();
                return new AdbReverseResult(
                    false,
                    $"adb reverse tcp:{port} falhou: {detail}",
                    TransportPorts);
            }
        }

        ProcessRunResult reverseList;
        try
        {
            reverseList = await _runner.RunAsync(
                    status.AdbPath,
                    ["-s", status.DeviceSerial, "reverse", "--list"],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var detail = _sessionToken.Redact(exception.Message);
            _sessionToken.Clear();
            return new AdbReverseResult(
                false,
                $"Falha ao confirmar adb reverse: {detail}",
                TransportPorts);
        }
        if (!reverseList.Succeeded || TransportPorts.Any(
                port => !reverseList.StandardOutput.Contains(
                    $"tcp:{port} tcp:{port}", StringComparison.Ordinal)))
        {
            _sessionToken.Clear();
            return new AdbReverseResult(
                false,
                "O adb não confirmou todos os redirecionamentos de desenvolvimento.",
                TransportPorts);
        }

        ProcessRunResult launch;
        try
        {
            launch = await _runner.RunAsync(
                    status.AdbPath,
                    [
                        "-s", status.DeviceSerial,
                        "shell", "am", "start", "-S",
                        "-n", "com.panelos.panelhome/.MainActivity",
                        "--es", "transport", "adb",
                        "--es", DevelopmentSessionToken.AndroidIntentExtra, token,
                    ],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var detail = _sessionToken.Redact(exception.Message);
            _sessionToken.Clear();
            return new AdbReverseResult(
                false,
                $"Falha ao iniciar PanelHome: {detail}",
                TransportPorts);
        }
        if (!launch.Succeeded)
        {
            var detail = _sessionToken.Redact(Describe(launch));
            _sessionToken.Clear();
            return new AdbReverseResult(
                false,
                $"PanelHome não iniciou pelo fluxo ADB: {detail}",
                TransportPorts);
        }

        return new AdbReverseResult(
            true,
            $"Sessão ADB autenticada preparada em {status.DeviceSerial}; " +
            $"USB reverse ativo para {string.Join(" e ", TransportPorts)}.",
            TransportPorts);
    }

    internal static IReadOnlyList<(string Serial, string State)> ParseDevices(
        string output)
    {
        var devices = new List<(string Serial, string State)>();
        foreach (var line in output.Split(
                     '\n',
                     StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith('*'))
            {
                continue;
            }

            var parts = line.Split(
                ['\t', ' '],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                devices.Add((parts[0], parts[1]));
            }
        }

        return devices;
    }

    /// <summary>
    /// Only a USB target counts. An emulator or a network target would connect,
    /// but it is not the transport this development setup is about.
    /// </summary>
    internal static bool IsUsbSerial(string serial) =>
        !serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase) &&
        !serial.StartsWith("adb-", StringComparison.OrdinalIgnoreCase) &&
        !serial.Contains(':', StringComparison.Ordinal) &&
        !serial.EndsWith(".local", StringComparison.OrdinalIgnoreCase);

    private static AdbStatus Interpret(
        string adbPath,
        IReadOnlyList<(string Serial, string State)> devices)
    {
        var usable = devices.Where(device => IsUsbSerial(device.Serial)).ToList();
        if (usable.Count == 0)
        {
            var detail = devices.Count == 0
                ? "Nenhum dispositivo conectado."
                : "Nenhum dispositivo USB: apenas emuladores ou alvos por rede.";
            return new AdbStatus
            {
                AdbFound = true,
                AdbPath = adbPath,
                Device = AdbDeviceState.NoDevice,
                Detail = detail,
            };
        }

        if (usable.Count > 1)
        {
            return new AdbStatus
            {
                AdbFound = true,
                AdbPath = adbPath,
                Device = AdbDeviceState.MultipleDevices,
                Detail =
                    $"{usable.Count} dispositivos conectados; deixe apenas um.",
            };
        }

        var (serial, state) = usable[0];
        return state.ToLowerInvariant() switch
        {
            "device" => new AdbStatus
            {
                AdbFound = true,
                AdbPath = adbPath,
                Device = AdbDeviceState.Ready,
                DeviceSerial = serial,
            },
            "unauthorized" => new AdbStatus
            {
                AdbFound = true,
                AdbPath = adbPath,
                Device = AdbDeviceState.Unauthorized,
                DeviceSerial = serial,
                Detail = "Autorize a depuração USB no tablet.",
            },
            "offline" => new AdbStatus
            {
                AdbFound = true,
                AdbPath = adbPath,
                Device = AdbDeviceState.Offline,
                DeviceSerial = serial,
                Detail = "O dispositivo está offline.",
            },
            _ => new AdbStatus
            {
                AdbFound = true,
                AdbPath = adbPath,
                Device = AdbDeviceState.Unknown,
                DeviceSerial = serial,
                Detail = $"Estado reportado pelo adb: {state}.",
            },
        };
    }

    private static string Describe(ProcessRunResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return string.IsNullOrEmpty(text) ? $"código {result.ExitCode}" : text;
    }
}
