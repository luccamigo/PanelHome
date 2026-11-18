using System.Collections.ObjectModel;
using System.Globalization;
using PanelOS.ControlCenter.Mvvm;
using PanelOS.ControlCenter.Services;
using PanelOS.Windows.Wifi;
using Strings = PanelOS.ControlCenter.Resources.ControlCenterStrings;

namespace PanelOS.ControlCenter.ViewModels;

public sealed record WifiDeviceItem(
    string Fingerprint,
    string Name,
    string ShortFingerprint,
    string StateText,
    PanelStatusLevel StateLevel,
    bool Connected,
    string CreatedText,
    bool CanRevoke);

/// <summary>
/// The Wi-Fi page: open a pairing invitation (QR, or a typed code when the camera cannot read it), show the request that
/// arrives with the device's name and FULL key fingerprint, let the operator approve or refuse it, and list and revoke
/// paired devices. Everything goes through the user-only admin pipe of the process that owns the WiFi identity; when that
/// process is not running (the default) the page says the Wi-Fi is off, because it is.
/// </summary>
/// <remarks>
/// The QR / code is a one-use secret. It is kept in memory only while the invitation is open, never copied, logged or
/// written anywhere, and is dropped as soon as the host stops reporting it.
/// </remarks>
/// <summary>One block of a fingerprint or of the typed code. Alternating blocks get another colour (never the only signal).</summary>
public sealed record WifiBlock(string Text, bool Alternate);

public sealed record WifiBlockRow(IReadOnlyList<WifiBlock> Blocks);

public sealed class WifiPairingViewModel : ObservableObject
{
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SlowPoll = TimeSpan.FromSeconds(2);

    private readonly IWifiAdminClient _client;
    private readonly IQrEncoder _qr;
    private readonly IConfirmationService _confirmation;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private bool _isAvailable;
    private string _windowText = string.Empty;
    private string _endpointText = string.Empty;
    private bool _hasInvitation;
    private bool _isQr;
    private IReadOnlyList<string>? _qrModules;
    private string? _typedCode;
    private string _invitationExpiresText = string.Empty;
    private string _invitationInstruction = string.Empty;
    private bool _hasPending;
    private int _pendingSecondsLeft;
    private IReadOnlyList<WifiBlockRow> _pendingFingerprintRows = [];
    private IReadOnlyList<WifiBlockRow> _typedCodeRows = [];
    private IReadOnlyList<WifiBlockRow> _hostFingerprintRows = [];
    private bool _isEnlarged;
    private string _pendingLabel = string.Empty;
    private string _pendingFingerprint = string.Empty;
    private string _pendingRawFingerprint = string.Empty;
    private string _pendingRespondText = string.Empty;
    private string? _outcomeText;
    private string? _errorText;

    public WifiPairingViewModel(IWifiAdminClient client, IQrEncoder qr, IConfirmationService confirmation,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(qr);
        ArgumentNullException.ThrowIfNull(confirmation);
        _client = client; _qr = qr; _confirmation = confirmation;
        _delay = delay ?? Task.Delay;
        OpenQrCommand = new AsyncRelayCommand(ct => OpenAsync(WifiAdminProtocol.ModeQr, ct), () => IsAvailable && !HasInvitation);
        OpenCodeCommand = new AsyncRelayCommand(ct => OpenAsync(WifiAdminProtocol.ModeCode, ct), () => IsAvailable);
        CancelCommand = new AsyncRelayCommand(ct => SendAsync(new(WifiAdminProtocol.Version, WifiAdminProtocol.CancelPairing), ct), () => HasInvitation);
        ApproveCommand = new AsyncRelayCommand(ct => DecideAsync(WifiAdminProtocol.Approve, ct), () => HasPending);
        DenyCommand = new AsyncRelayCommand(ct => DecideAsync(WifiAdminProtocol.Deny, ct), () => HasPending);
        RevokeCommand = new RelayCommand<WifiDeviceItem>(RevokeFireAndForget, item => item?.CanRevoke == true);
        EnlargeCommand = new RelayCommand(() => IsEnlarged = HasQrInvitation);
        CloseEnlargedCommand = new RelayCommand(() => IsEnlarged = false);
    }

    public ObservableCollection<WifiDeviceItem> Devices { get; } = [];
    public AsyncRelayCommand OpenQrCommand { get; }
    public AsyncRelayCommand OpenCodeCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public AsyncRelayCommand ApproveCommand { get; }
    public AsyncRelayCommand DenyCommand { get; }
    public RelayCommand<WifiDeviceItem> RevokeCommand { get; }
    public RelayCommand EnlargeCommand { get; }
    public RelayCommand CloseEnlargedCommand { get; }

    /// <summary>The QR full screen over the page ("Ampliar"); Esc or a click goes back. Never outlives the invitation.</summary>
    public bool IsEnlarged { get => _isEnlarged; private set => SetProperty(ref _isEnlarged, value); }

    /// <summary>The invitation (QR or code) is on screen; once a device has asked, the approval card takes its place.</summary>
    public bool ShowInvitation => HasInvitation && !HasPending;
    public bool HasQrInvitation => ShowInvitation && IsQr && QrModules is not null;
    /// <summary>The window card (window time and the buttons that open an invitation) is hidden while an invitation is open, so the QR fits.</summary>
    public bool ShowWindowCard => !HasInvitation;

    public bool IsAvailable { get => _isAvailable; private set { if (SetProperty(ref _isAvailable, value)) { OnPropertyChanged(nameof(IsOff)); RaiseCommands(); } } }
    public bool IsOff => !_isAvailable;
    public string WindowText { get => _windowText; private set => SetProperty(ref _windowText, value); }
    public string EndpointText { get => _endpointText; private set => SetProperty(ref _endpointText, value); }
    public bool HasInvitation { get => _hasInvitation; private set { if (SetProperty(ref _hasInvitation, value)) { OnPropertyChanged(nameof(CanOpenInvitation)); OnPropertyChanged(nameof(ShowInvitation)); OnPropertyChanged(nameof(ShowWindowCard)); OnPropertyChanged(nameof(HasQrInvitation)); if (!value) IsEnlarged = false; RaiseCommands(); } } }
    public bool CanOpenInvitation => IsAvailable && !HasInvitation;
    public bool IsQr { get => _isQr; private set { if (SetProperty(ref _isQr, value)) { OnPropertyChanged(nameof(IsCode)); OnPropertyChanged(nameof(HasQrInvitation)); } } }
    public bool IsCode => !_isQr;
    public IReadOnlyList<string>? QrModules { get => _qrModules; private set { if (SetProperty(ref _qrModules, value)) OnPropertyChanged(nameof(HasQrInvitation)); } }
    public string? TypedCode { get => _typedCode; private set { if (SetProperty(ref _typedCode, value)) TypedCodeRows = value is null ? [] : CodeRows(value); } }
    /// <summary>The typed code as 20 groups of 5, five per line over four lines. Not selectable and not copyable: it is a one-use secret.</summary>
    public IReadOnlyList<WifiBlockRow> TypedCodeRows { get => _typedCodeRows; private set => SetProperty(ref _typedCodeRows, value); }
    public string InvitationExpiresText { get => _invitationExpiresText; private set => SetProperty(ref _invitationExpiresText, value); }
    public string InvitationInstruction { get => _invitationInstruction; private set => SetProperty(ref _invitationInstruction, value); }
    public bool HasPending { get => _hasPending; private set { if (SetProperty(ref _hasPending, value)) { OnPropertyChanged(nameof(ShowInvitation)); OnPropertyChanged(nameof(HasQrInvitation)); RaiseCommands(); } } }
    /// <summary>Seconds the operator still has (0..60) and the same as a 0..1 bar; the last ten seconds use the warning colour.</summary>
    public int PendingSecondsLeft { get => _pendingSecondsLeft; private set { if (SetProperty(ref _pendingSecondsLeft, value)) { OnPropertyChanged(nameof(PendingProgress)); OnPropertyChanged(nameof(PendingIsUrgent)); } } }
    public double PendingProgress => Math.Clamp(_pendingSecondsLeft / (double)ApproveWindowSeconds, 0, 1);
    public bool PendingIsUrgent => _pendingSecondsLeft is > 0 and <= 10;
    public IReadOnlyList<WifiBlockRow> PendingFingerprintRows { get => _pendingFingerprintRows; private set => SetProperty(ref _pendingFingerprintRows, value); }
    /// <summary>Read aloud when the request arrives (the card is an assertive live region).</summary>
    public string PendingAnnouncement => HasPending ? Strings.Format("WifiPendingAnnouncementFormat", PendingLabel) : string.Empty;
    /// <summary>This PC's own fingerprint, to compare with what the tablet shows for "the PC".</summary>
    public IReadOnlyList<WifiBlockRow> HostFingerprintRows { get => _hostFingerprintRows; private set { if (SetProperty(ref _hostFingerprintRows, value)) OnPropertyChanged(nameof(HasHostFingerprint)); } }
    public bool HasHostFingerprint => _hostFingerprintRows.Count > 0;
    public string PendingLabel { get => _pendingLabel; private set => SetProperty(ref _pendingLabel, value); }
    public string PendingFingerprint { get => _pendingFingerprint; private set => SetProperty(ref _pendingFingerprint, value); }
    public string PendingRespondText { get => _pendingRespondText; private set => SetProperty(ref _pendingRespondText, value); }
    public string? OutcomeText { get => _outcomeText; private set { if (SetProperty(ref _outcomeText, value)) OnPropertyChanged(nameof(HasOutcome)); } }
    public bool HasOutcome => !string.IsNullOrEmpty(_outcomeText);
    public string? ErrorText { get => _errorText; private set { if (SetProperty(ref _errorText, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(_errorText);

    /// <summary>Polls while the page lives: quickly while an invitation is open, slowly otherwise.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await RefreshAsync(cancellationToken).ConfigureAwait(true); }
            catch (OperationCanceledException) { break; }
            try { await _delay(HasInvitation || HasPending ? FastPoll : SlowPoll, cancellationToken).ConfigureAwait(true); }
            catch (OperationCanceledException) { break; }
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken) =>
        SendAsync(new(WifiAdminProtocol.Version, WifiAdminProtocol.Status), cancellationToken);

    private Task OpenAsync(string mode, CancellationToken cancellationToken)
    {
        OutcomeText = null;
        return SendAsync(new(WifiAdminProtocol.Version, WifiAdminProtocol.OpenPairing, mode), cancellationToken);
    }

    private Task DecideAsync(string type, CancellationToken cancellationToken) =>
        // The fingerprint sent back is the one the operator was shown: an approval of a different request is refused.
        SendAsync(new(WifiAdminProtocol.Version, type, Fingerprint: _pendingRawFingerprint), cancellationToken);

    private void RevokeFireAndForget(WifiDeviceItem? item)
    {
        if (item is null) return;
        if (!_confirmation.Confirm(Strings.Get("WifiRevokeConfirmTitle"), Strings.Format("WifiRevokeConfirmFormat", item.Name))) return;
        _ = SendAsync(new(WifiAdminProtocol.Version, WifiAdminProtocol.Revoke, Fingerprint: item.Fingerprint), CancellationToken.None);
    }

    private async Task SendAsync(WifiAdminRequest request, CancellationToken cancellationToken)
    {
        var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(true);
        Apply(response, request.Type != WifiAdminProtocol.Status);
    }

    private void Apply(WifiAdminResponse response, bool reportError)
    {
        var status = response.Status;
        if (status is null || !status.Running)
        {
            Clear();
            // "Off" is the normal state, not an error; only a command that failed for another reason is reported.
            ErrorText = reportError && response.Error is { } code && code != WifiAdminProtocol.ErrorUnavailable ? ErrorMessage(code) : null;
            return;
        }
        IsAvailable = true;
        ErrorText = response.Ok ? null : ErrorMessage(response.Error);
        WindowText = Strings.Format("WifiWindowFormat", Clock(status.WindowSecondsLeft));
        EndpointText = Strings.Format("WifiEndpointFormat", status.BindAddress, status.SessionPort);
        HostFingerprintRows = status.HostId is { Length: 64 } hostId ? FingerprintRows(hostId) : [];
        ApplyPairing(status.Pairing);
        ApplyDevices(status.Devices);
    }

    private void ApplyPairing(WifiAdminPairing? pairing)
    {
        var open = pairing is { Outcome: "Pending" };
        HasInvitation = open;
        if (!open)
        {
            // The secret is dropped the moment the host stops reporting it.
            QrModules = null; TypedCode = null; InvitationExpiresText = string.Empty; InvitationInstruction = string.Empty;
            HasPending = false; PendingLabel = string.Empty; PendingFingerprint = string.Empty; _pendingRawFingerprint = string.Empty;
            PendingFingerprintRows = []; PendingSecondsLeft = 0;
            if (pairing is not null) OutcomeText = OutcomeMessage(pairing.Outcome);
            return;
        }
        IsQr = pairing!.Mode == WifiAdminProtocol.ModeQr;
        var lifetime = Clock(pairing.ExpiresInSeconds);
        InvitationExpiresText = Strings.Format("WifiInvitationExpiresFormat", lifetime);
        InvitationInstruction = Strings.Format(IsQr ? "WifiQrInstructionFormat" : "WifiCodeInstructionFormat", Clock(WifiPairingMinutes * 60));
        if (IsQr)
        {
            if (QrModules is null && pairing.Qr is not null) QrModules = _qr.Encode(pairing.Qr);
            TypedCode = null;
        }
        else
        {
            QrModules = null;
            TypedCode = pairing.Code;
        }
        // At zero the buttons are gone at once (the host reports the outcome a moment later): "Approve" is never clickable
        // with no time left.
        if (pairing.Pending is { ApproveSecondsLeft: > 0 } pending)
        {
            HasPending = true;
            PendingLabel = pending.Label;
            _pendingRawFingerprint = pending.Fingerprint;
            PendingFingerprint = GroupFingerprint(pending.Fingerprint);
            PendingFingerprintRows = FingerprintRows(pending.Fingerprint);
            PendingSecondsLeft = Math.Min(ApproveWindowSeconds, pending.ApproveSecondsLeft);
            PendingRespondText = Strings.Format("WifiApproveRespondFormat", Clock(pending.ApproveSecondsLeft));
            OnPropertyChanged(nameof(PendingAnnouncement));
        }
        else
        {
            HasPending = false; PendingLabel = string.Empty; PendingFingerprint = string.Empty; _pendingRawFingerprint = string.Empty;
            PendingFingerprintRows = []; PendingSecondsLeft = 0;
            if (pairing.Pending is not null) OutcomeText = Strings.Get("WifiOutcomeDenied");
        }
    }

    private const int ApproveWindowSeconds = 60;

    private const int WifiPairingMinutes = 10;

    private void ApplyDevices(IReadOnlyList<WifiAdminDevice> devices)
    {
        var next = devices.Select(device => new WifiDeviceItem(
            device.Fingerprint,
            string.IsNullOrWhiteSpace(device.Label) ? Strings.Get("WifiDeviceUnnamed") : device.Label,
            ShortFingerprint(device.Fingerprint),
            device.State switch
            {
                "Active" => Strings.Get("WifiDeviceStateActive"),
                "Issued" => Strings.Get("WifiDeviceStateIssued"),
                "Revoked" => Strings.Get("WifiDeviceStateRevoked"),
                _ => device.State,
            },
            device.State switch { "Active" => PanelStatusLevel.Good, "Issued" => PanelStatusLevel.Warning, _ => PanelStatusLevel.Neutral },
            device.Connected,
            device.CreatedAt is { } created ? created.ToLocalTime().ToString("g", Resources.ControlCenterStrings.FormatCulture) : string.Empty,
            device.State != "Revoked")).ToArray();
        if (Devices.SequenceEqual(next)) return;
        Devices.Clear();
        foreach (var item in next) Devices.Add(item);
        RevokeCommand.RaiseCanExecuteChanged();
    }

    private void Clear()
    {
        IsAvailable = false; HasInvitation = false; HasPending = false; IsEnlarged = false;
        HostFingerprintRows = []; PendingFingerprintRows = []; PendingSecondsLeft = 0;
        WindowText = string.Empty; EndpointText = string.Empty; QrModules = null; TypedCode = null;
        InvitationExpiresText = string.Empty; InvitationInstruction = string.Empty;
        PendingLabel = string.Empty; PendingFingerprint = string.Empty; _pendingRawFingerprint = string.Empty;
        Devices.Clear();
    }

    private void RaiseCommands()
    {
        OpenQrCommand.RaiseCanExecuteChanged(); OpenCodeCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged();
        ApproveCommand.RaiseCanExecuteChanged(); DenyCommand.RaiseCanExecuteChanged();
    }

    public static string Clock(int seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>The whole 256-bit fingerprint in eight groups of eight hex digits, UPPERCASE like the tablet shows it. Never abbreviated.</summary>
    public static string GroupFingerprint(string hex)
    {
        hex = hex.ToUpperInvariant();
        return string.Join(' ', Enumerable.Range(0, hex.Length / 8).Select(i => hex.Substring(i * 8, 8)));
    }

    /// <summary>Two rows of four blocks of eight, uppercase: the same line break as the tablet. Alternating blocks change colour.</summary>
    public static IReadOnlyList<WifiBlockRow> FingerprintRows(string hex)
    {
        var blocks = GroupFingerprint(hex).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select((text, index) => new WifiBlock(text, index % 2 == 1)).ToArray();
        return [.. blocks.Chunk(4).Select(row => new WifiBlockRow(row))];
    }

    /// <summary>The typed code regrouped as 20 groups of five, five per line (dashes and spaces from the host are dropped).</summary>
    public static IReadOnlyList<WifiBlockRow> CodeRows(string code)
    {
        var clean = new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var blocks = clean.Chunk(5).Select((chunk, index) => new WifiBlock(new string(chunk), index % 2 == 1)).ToArray();
        return [.. blocks.Chunk(5).Select(row => new WifiBlockRow(row))];
    }

    /// <summary>Uppercase, two groups of eight (16 characters): enough to tell devices apart in the list, never used to approve.</summary>
    public static string ShortFingerprint(string fingerprint)
    {
        var upper = fingerprint.ToUpperInvariant();
        return upper.Length >= 16 ? upper[..8] + " " + upper[8..16] : upper;
    }

    private static string OutcomeMessage(string outcome) => outcome switch
    {
        "Approved" => Strings.Get("WifiOutcomeApproved"),
        "Denied" => Strings.Get("WifiOutcomeDenied"),
        "Expired" => Strings.Get("WifiOutcomeExpired"),
        "Cancelled" => Strings.Get("WifiOutcomeCancelled"),
        "TooManyInvalid" => Strings.Get("WifiOutcomeTooMany"),
        "Abandoned" => Strings.Get("WifiOutcomeAbandoned"),
        "LimitReached" => Strings.Get("WifiOutcomeLimit"),
        _ => string.Empty,
    };

    private static string ErrorMessage(string? code) => code switch
    {
        WifiAdminProtocol.ErrorPairingOpen => Strings.Get("WifiErrorPairingOpen"),
        WifiAdminProtocol.ErrorNoRequest => Strings.Get("WifiErrorNoRequest"),
        WifiAdminProtocol.ErrorUnknownDevice => Strings.Get("WifiErrorUnknownDevice"),
        WifiAdminProtocol.ErrorWindowClosed => Strings.Get("WifiErrorWindowClosed"),
        WifiAdminProtocol.ErrorInvalid or WifiAdminProtocol.ErrorUnsupported => Strings.Get("WifiErrorInvalid"),
        WifiAdminProtocol.ErrorUnavailable => Strings.Get("WifiErrorUnavailable"),
        _ => Strings.Get("WifiErrorFailed"),
    };
}
