using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using PanelOS.DisplayBroker.Protocol;

namespace PanelDisplayBroker;

/// <summary>Where the broker was when something failed.</summary>
internal enum BrokerStage
{
    Startup,
    Connect,
    Handshake,
    Serve,
}

internal static class BrokerExit
{
    /// <summary>
    /// Maps a failure to an exit code. An I/O error once the handshake is accepted is the host closing
    /// the lease (an orderly end); an I/O error before that is a failure and keeps its own code.
    /// </summary>
    internal static (int Code, string Reason) Classify(Exception exception, BrokerStage stage) => exception switch
    {
        UnauthorizedAccessException => (
            PanelDisplayBrokerExitCodes.PipeOwnerRejected,
            "access denied or the pipe owner was rejected"),
        TimeoutException => (
            PanelDisplayBrokerExitCodes.PipeConnectFailed,
            "timed out connecting to the host pipe"),
        JsonException or InvalidDataException => (
            PanelDisplayBrokerExitCodes.ProtocolError,
            "malformed data from the host"),
        IOException => stage switch
        {
            BrokerStage.Serve => (PanelDisplayBrokerExitCodes.Ok, "the host closed the pipe"),
            BrokerStage.Handshake => (PanelDisplayBrokerExitCodes.HandshakeFailed, "I/O error during the hello exchange"),
            _ => (PanelDisplayBrokerExitCodes.PipeConnectFailed, "I/O error connecting to the host pipe"),
        },
        _ => (PanelDisplayBrokerExitCodes.Unexpected, "unexpected exception"),
    };
}

/// <summary>
/// The broker is an elevated client of a pipe that a medium-integrity host created with
/// <c>CurrentUserOnly</c>. .NET's own client check (<c>PipeOptions.CurrentUserOnly</c>) compares the pipe
/// owner with <c>WindowsIdentity.Owner</c>, which for an elevated token is BUILTIN\Administrators, while
/// the host's pipe is owned by the user SID; so every connection was refused with "not owned by the
/// current user" (the PAN-1 run on this PC logged the same exception for elevated client to normal
/// server). The same-user guarantee is kept here, with the right comparison: the owner must be the
/// user SID, or the token owner (an elevated host).
/// </summary>
internal static class BrokerPipeOwner
{
    internal static bool IsTrusted(
        SecurityIdentifier? owner,
        SecurityIdentifier? user,
        SecurityIdentifier? tokenOwner) =>
        owner is not null &&
        ((user is not null && owner.Equals(user)) ||
         (tokenOwner is not null && owner.Equals(tokenOwner)));

    internal static void Verify(PipeStream connected)
    {
        var owner = connected.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        using var identity = WindowsIdentity.GetCurrent();
        if (!IsTrusted(owner, identity.User, identity.Owner))
        {
            throw new UnauthorizedAccessException(
                "The host pipe is not owned by the current user or the current token owner.");
        }
    }
}
