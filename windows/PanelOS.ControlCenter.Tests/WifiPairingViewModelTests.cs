using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.ControlCenter.Services;
using PanelOS.ControlCenter.ViewModels;
using PanelOS.Windows.Wifi;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class WifiPairingViewModelTests
{
    private const string Fingerprint = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    private sealed class FakeWifiAdminClient : IWifiAdminClient
    {
        public readonly List<WifiAdminRequest> Requests = [];
        public Func<WifiAdminRequest, WifiAdminResponse> Handler { get; set; } = _ => new WifiAdminResponse(false, WifiAdminProtocol.ErrorUnavailable, null);
        public Task<WifiAdminResponse> SendAsync(WifiAdminRequest request, CancellationToken cancellationToken)
        { Requests.Add(request); return Task.FromResult(Handler(request)); }
    }

    private sealed class FakeQr : IQrEncoder
    {
        public readonly List<string> Encoded = [];
        public IReadOnlyList<string> Encode(string text) { Encoded.Add(text); return ["111", "101", "111"]; }
    }

    private sealed class FakeConfirmation(bool answer) : IConfirmationService
    {
        public readonly List<(string Title, string Message)> Asked = [];
        public bool Confirm(string title, string message) { Asked.Add((title, message)); return answer; }
    }

    private static WifiAdminResponse Running(WifiAdminPairing? pairing = null, params WifiAdminDevice[] devices) =>
        new(true, null, new WifiAdminStatus(true, 170, "192.168.15.4", 40002, 40001, pairing, devices));

    private static WifiAdminPairing Open(string mode = "qr", WifiAdminPending? pending = null) =>
        new(mode, "Pending", 600, mode == "qr" ? "{\"type\":\"PanelOSPair/1\"}" : null, mode == "code" ? "AAAAA-BBBBB" : null, pending);

    private static (WifiPairingViewModel ViewModel, FakeWifiAdminClient Client, FakeQr Qr, FakeConfirmation Confirmation) Create(bool confirm = true)
    {
        var client = new FakeWifiAdminClient(); var qr = new FakeQr(); var confirmation = new FakeConfirmation(confirm);
        return (new WifiPairingViewModel(client, qr, confirmation, (_, _) => Task.CompletedTask), client, qr, confirmation);
    }

    [TestMethod]
    public async Task Without_a_running_wifi_process_the_page_says_it_is_off_and_nothing_is_actionable()
    {
        var (viewModel, _, _, _) = Create();
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.IsTrue(viewModel.IsOff); Assert.IsFalse(viewModel.IsAvailable);
        Assert.IsFalse(viewModel.HasError, "Off is the normal state, not an error.");
        Assert.IsFalse(viewModel.OpenQrCommand.CanExecute(null)); Assert.IsFalse(viewModel.OpenCodeCommand.CanExecute(null));
        Assert.AreEqual(0, viewModel.Devices.Count);
    }

    [TestMethod]
    public async Task A_running_window_shows_its_time_and_endpoint_and_allows_a_new_invitation()
    {
        var (viewModel, client, _, _) = Create();
        client.Handler = _ => Running();
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.IsTrue(viewModel.IsAvailable);
        StringAssert.Contains(viewModel.WindowText, "2:50");
        StringAssert.Contains(viewModel.EndpointText, "192.168.15.4"); StringAssert.Contains(viewModel.EndpointText, "40002");
        Assert.IsTrue(viewModel.OpenQrCommand.CanExecute(null)); Assert.IsFalse(viewModel.CancelCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task Opening_an_invitation_asks_for_it_by_mode_and_shows_the_qr_for_ten_minutes()
    {
        var (viewModel, client, qr, _) = Create();
        client.Handler = request => request.Type == WifiAdminProtocol.OpenPairing ? Running(Open()) : Running();
        await viewModel.RefreshAsync(CancellationToken.None);
        await viewModel.OpenQrCommand.ExecuteAsync(CancellationToken.None);
        Assert.AreEqual(WifiAdminProtocol.ModeQr, client.Requests.Last().Mode);
        Assert.IsTrue(viewModel.HasInvitation); Assert.IsTrue(viewModel.IsQr);
        Assert.IsNotNull(viewModel.QrModules); Assert.IsNull(viewModel.TypedCode);
        CollectionAssert.AreEqual(new[] { "{\"type\":\"PanelOSPair/1\"}" }, qr.Encoded);
        StringAssert.Contains(viewModel.InvitationInstruction, "10:00");
        StringAssert.Contains(viewModel.InvitationExpiresText, "10:00");
        Assert.IsFalse(viewModel.OpenQrCommand.CanExecute(null)); Assert.IsTrue(viewModel.CancelCommand.CanExecute(null));
        // The QR is drawn from the first answer; the same invitation is not re-encoded on every poll.
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.AreEqual(1, qr.Encoded.Count);
    }

    [TestMethod]
    public async Task Code_mode_shows_the_typed_code_and_no_qr()
    {
        var (viewModel, client, qr, _) = Create();
        client.Handler = request => request.Type == WifiAdminProtocol.OpenPairing ? Running(Open("code")) : Running();
        await viewModel.RefreshAsync(CancellationToken.None);
        await viewModel.OpenCodeCommand.ExecuteAsync(CancellationToken.None);
        Assert.AreEqual(WifiAdminProtocol.ModeCode, client.Requests.Last().Mode);
        Assert.IsTrue(viewModel.IsCode); Assert.AreEqual("AAAAA-BBBBB", viewModel.TypedCode);
        Assert.IsNull(viewModel.QrModules); Assert.AreEqual(0, qr.Encoded.Count);
    }

    [TestMethod]
    public async Task The_secret_is_dropped_the_moment_the_host_stops_reporting_the_invitation()
    {
        var (viewModel, client, _, _) = Create();
        client.Handler = _ => Running(Open());
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.IsNotNull(viewModel.QrModules);
        client.Handler = _ => Running(new WifiAdminPairing("qr", "Approved", 0, null, null, null));
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.IsNull(viewModel.QrModules); Assert.IsNull(viewModel.TypedCode); Assert.IsFalse(viewModel.HasInvitation);
        StringAssert.Contains(viewModel.OutcomeText!, "pareado");
    }

    [TestMethod]
    public async Task A_request_shows_the_name_and_the_full_fingerprint_in_eight_groups_and_approval_sends_the_exact_fingerprint()
    {
        var (viewModel, client, _, _) = Create();
        client.Handler = _ => Running(Open(pending: new WifiAdminPending("Tablet da sala", Fingerprint, 51)));
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.IsTrue(viewModel.HasPending);
        Assert.AreEqual("Tablet da sala", viewModel.PendingLabel);
        Assert.AreEqual("01234567 89ABCDEF 01234567 89ABCDEF 01234567 89ABCDEF 01234567 89ABCDEF", viewModel.PendingFingerprint);
        Assert.AreEqual(64, viewModel.PendingFingerprint.Replace(" ", "").Length, "Never abbreviated.");
        StringAssert.Contains(viewModel.PendingRespondText, "0:51");
        await viewModel.ApproveCommand.ExecuteAsync(CancellationToken.None);
        var sent = client.Requests.Last();
        Assert.AreEqual(WifiAdminProtocol.Approve, sent.Type); Assert.AreEqual(Fingerprint, sent.Fingerprint);
        await viewModel.DenyCommand.ExecuteAsync(CancellationToken.None);
        Assert.AreEqual(WifiAdminProtocol.Deny, client.Requests.Last().Type);
    }

    [TestMethod]
    public async Task Approve_and_deny_are_unavailable_without_a_pending_request()
    {
        var (viewModel, client, _, _) = Create();
        client.Handler = _ => Running(Open());
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.IsFalse(viewModel.ApproveCommand.CanExecute(null)); Assert.IsFalse(viewModel.DenyCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task Devices_are_listed_with_their_state_and_a_revoked_one_cannot_be_revoked_again()
    {
        var (viewModel, client, _, _) = Create();
        var created = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
        client.Handler = _ => Running(null,
            new WifiAdminDevice(Fingerprint, "Tablet", "Active", created, true),
            new WifiAdminDevice(new string('A', 64), null, "Issued", created, false),
            new WifiAdminDevice(new string('B', 64), "Antigo", "Revoked", created, false));
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.AreEqual(3, viewModel.Devices.Count);
        Assert.AreEqual("Ativo", viewModel.Devices[0].StateText); Assert.IsTrue(viewModel.Devices[0].Connected);
        Assert.AreEqual("Aparelho sem nome", viewModel.Devices[1].Name);
        Assert.AreEqual("01234567 89ABCDEF", viewModel.Devices[0].ShortFingerprint, "Uppercase, two groups of eight.");
        Assert.IsTrue(viewModel.RevokeCommand.CanExecute(viewModel.Devices[0]));
        Assert.IsFalse(viewModel.RevokeCommand.CanExecute(viewModel.Devices[2]));
    }

    [TestMethod]
    public async Task Revoking_asks_for_confirmation_naming_the_device_and_only_then_sends_the_fingerprint()
    {
        var (declined, declinedClient, _, declinedConfirmation) = Create(confirm: false);
        declinedClient.Handler = _ => Running(null, new WifiAdminDevice(Fingerprint, "Tablet da sala", "Active", null, false));
        await declined.RefreshAsync(CancellationToken.None);
        declined.RevokeCommand.Execute(declined.Devices[0]);
        StringAssert.Contains(declinedConfirmation.Asked.Single().Message, "Tablet da sala");
        Assert.IsFalse(declinedClient.Requests.Any(r => r.Type == WifiAdminProtocol.Revoke), "Declining sends nothing.");

        var (viewModel, client, _, confirmation) = Create(confirm: true);
        client.Handler = _ => Running(null, new WifiAdminDevice(Fingerprint, "Tablet da sala", "Active", null, false));
        await viewModel.RefreshAsync(CancellationToken.None);
        viewModel.RevokeCommand.Execute(viewModel.Devices[0]);
        Assert.AreEqual(1, confirmation.Asked.Count);
        await Task.Delay(50);
        var revoke = client.Requests.Single(r => r.Type == WifiAdminProtocol.Revoke);
        Assert.AreEqual(Fingerprint, revoke.Fingerprint);
    }

    [TestMethod]
    public async Task A_refused_command_reports_a_plain_reason_and_keeps_the_window()
    {
        var (viewModel, client, _, _) = Create();
        client.Handler = request => request.Type == WifiAdminProtocol.OpenPairing
            ? new WifiAdminResponse(false, WifiAdminProtocol.ErrorPairingOpen, Running().Status) : Running();
        await viewModel.RefreshAsync(CancellationToken.None);
        await viewModel.OpenQrCommand.ExecuteAsync(CancellationToken.None);
        Assert.IsTrue(viewModel.HasError); StringAssert.Contains(viewModel.ErrorText!, "convite aberto");
        Assert.IsTrue(viewModel.IsAvailable);
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.IsFalse(viewModel.HasError, "A later good answer clears the error.");
    }

    [TestMethod]
    public async Task The_window_closing_turns_the_page_back_to_off_and_clears_everything()
    {
        var (viewModel, client, _, _) = Create();
        client.Handler = _ => Running(Open(pending: new WifiAdminPending("x", Fingerprint, 30)), new WifiAdminDevice(Fingerprint, "Tablet", "Active", null, false));
        await viewModel.RefreshAsync(CancellationToken.None);
        client.Handler = _ => new WifiAdminResponse(true, null, new WifiAdminStatus(false, 0, "192.168.15.4", 40002, 40001, null, []));
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.IsTrue(viewModel.IsOff); Assert.IsNull(viewModel.QrModules); Assert.IsFalse(viewModel.HasPending); Assert.AreEqual(0, viewModel.Devices.Count);
    }

    [TestMethod]
    public async Task Polling_is_fast_while_an_invitation_is_open_and_slow_otherwise_and_stops_on_cancellation()
    {
        var delays = new List<TimeSpan>(); using var cancel = new CancellationTokenSource(); var client = new FakeWifiAdminClient();
        var viewModel = new WifiPairingViewModel(client, new FakeQr(), new FakeConfirmation(true), (delay, _) =>
        { delays.Add(delay); if (delays.Count == 3) cancel.Cancel(); return Task.CompletedTask; });
        var step = 0;
        client.Handler = _ => ++step == 1 ? Running() : Running(Open());
        await viewModel.RunAsync(cancel.Token);
        Assert.AreEqual(3, delays.Count);
        Assert.AreEqual(TimeSpan.FromSeconds(2), delays[0]);
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), delays[1]);
    }

    [TestMethod]
    public void Fingerprint_grouping_and_clock_formatting()
    {
        Assert.AreEqual("0:00", WifiPairingViewModel.Clock(-5)); Assert.AreEqual("9:59", WifiPairingViewModel.Clock(599));
        Assert.AreEqual("AAAAAAAA BBBBBBBB", WifiPairingViewModel.GroupFingerprint("AAAAAAAABBBBBBBB"));
        Assert.AreEqual("AAAAAAAA BBBBBBBB", WifiPairingViewModel.GroupFingerprint("aaaaaaaabbbbbbbb"), "Uppercase on this side too, like the tablet.");
    }

    [TestMethod]
    public void The_fingerprint_is_two_rows_of_four_uppercase_blocks_with_alternating_colour_and_nothing_is_dropped()
    {
        var rows = WifiPairingViewModel.FingerprintRows(Fingerprint.ToLowerInvariant());
        Assert.AreEqual(2, rows.Count);
        Assert.IsTrue(rows.All(row => row.Blocks.Count == 4));
        var blocks = rows.SelectMany(row => row.Blocks).ToArray();
        Assert.AreEqual(Fingerprint, string.Concat(blocks.Select(block => block.Text)), "All 64 characters, uppercase.");
        CollectionAssert.AreEqual(new[] { false, true, false, true, false, true, false, true }, blocks.Select(block => block.Alternate).ToArray());
    }

    [TestMethod]
    public void The_typed_code_is_twenty_groups_of_five_five_per_line_over_four_lines()
    {
        var code = string.Join("-", Enumerable.Range(0, 20).Select(i => new string((char)('A' + i), 5)));
        var rows = WifiPairingViewModel.CodeRows(code);
        Assert.AreEqual(4, rows.Count);
        Assert.IsTrue(rows.All(row => row.Blocks.Count == 5));
        var blocks = rows.SelectMany(row => row.Blocks).ToArray();
        Assert.AreEqual(20, blocks.Length);
        Assert.AreEqual("AAAAA", blocks[0].Text); Assert.AreEqual("TTTTT", blocks[19].Text);
        Assert.AreEqual(code.Replace("-", ""), string.Concat(blocks.Select(block => block.Text)));
        Assert.IsTrue(blocks.Select(block => block.Alternate).SequenceEqual(Enumerable.Range(0, 20).Select(i => i % 2 == 1)));
    }

    [TestMethod]
    public async Task The_page_shows_this_pcs_own_fingerprint_from_the_status()
    {
        var (viewModel, client, _, _) = Create();
        client.Handler = _ => new WifiAdminResponse(true, null, new WifiAdminStatus(true, 170, "192.168.15.4", 40002, 40001, null, [], Fingerprint.ToLowerInvariant()));
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.IsTrue(viewModel.HasHostFingerprint);
        Assert.AreEqual(Fingerprint, string.Concat(viewModel.HostFingerprintRows.SelectMany(row => row.Blocks).Select(block => block.Text)));
        client.Handler = _ => Running();
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.IsFalse(viewModel.HasHostFingerprint, "An older host without the field simply shows nothing.");
    }

    [TestMethod]
    public async Task The_approval_card_takes_the_invitations_place_counts_down_and_has_no_buttons_at_zero()
    {
        var (viewModel, client, _, _) = Create();
        client.Handler = _ => Running(Open());
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.IsTrue(viewModel.ShowInvitation); Assert.IsFalse(viewModel.HasPending); Assert.IsFalse(viewModel.ShowWindowCard, "The QR gets the room.");

        client.Handler = _ => Running(Open(pending: new WifiAdminPending("Tablet", Fingerprint, 60)));
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.IsTrue(viewModel.HasPending); Assert.IsFalse(viewModel.ShowInvitation, "No stale QR next to the request.");
        Assert.AreEqual(1.0, viewModel.PendingProgress, 0.001); Assert.IsFalse(viewModel.PendingIsUrgent);
        StringAssert.Contains(viewModel.PendingAnnouncement, "Tablet");

        client.Handler = _ => Running(Open(pending: new WifiAdminPending("Tablet", Fingerprint, 30)));
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.AreEqual(0.5, viewModel.PendingProgress, 0.001);

        client.Handler = _ => Running(Open(pending: new WifiAdminPending("Tablet", Fingerprint, 10)));
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.IsTrue(viewModel.PendingIsUrgent, "Warning colour in the last ten seconds.");

        client.Handler = _ => Running(Open(pending: new WifiAdminPending("Tablet", Fingerprint, 0)));
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.IsFalse(viewModel.HasPending, "Approve is never clickable with no time left.");
        Assert.IsFalse(viewModel.ApproveCommand.CanExecute(null));
        Assert.IsTrue(viewModel.HasOutcome);
    }

    [TestMethod]
    public async Task Enlarging_the_qr_works_only_for_a_qr_invitation_and_never_outlives_it()
    {
        var (viewModel, client, _, _) = Create();
        viewModel.EnlargeCommand.Execute(null);
        Assert.IsFalse(viewModel.IsEnlarged, "Nothing to enlarge without a QR.");
        client.Handler = _ => Running(Open());
        await viewModel.RefreshAsync(CancellationToken.None);
        viewModel.EnlargeCommand.Execute(null);
        Assert.IsTrue(viewModel.IsEnlarged);
        viewModel.CloseEnlargedCommand.Execute(null);
        Assert.IsFalse(viewModel.IsEnlarged);
        viewModel.EnlargeCommand.Execute(null);
        client.Handler = _ => Running(new WifiAdminPairing("qr", "Cancelled", 0, null, null, null));
        await viewModel.RefreshAsync(CancellationToken.None);
        Assert.IsFalse(viewModel.IsEnlarged, "The full-screen QR goes away with the invitation.");
    }

    [TestMethod]
    public async Task Abandoned_and_over_limit_outcomes_have_their_own_plain_messages()
    {
        var (viewModel, client, _, _) = Create();
        client.Handler = _ => Running(new WifiAdminPairing("qr", "Abandoned", 0, null, null, null));
        await viewModel.RefreshAsync(CancellationToken.None);
        StringAssert.Contains(viewModel.OutcomeText!, "desistiu");
        client.Handler = _ => Running(new WifiAdminPairing("qr", "LimitReached", 0, null, null, null));
        await viewModel.RefreshAsync(CancellationToken.None);
        StringAssert.Contains(viewModel.OutcomeText!, "Limite de aparelhos");
    }
}