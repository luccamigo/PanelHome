using PanelHost.Protocol;
using PanelOS.Windows.StreamDeck.Dto;
using PanelOS.Windows.StreamDeck.Runtime;

namespace PanelHost.Services;

/// <summary>
/// Translates between the shared Stream Deck domain and the wire.
/// </summary>
/// <remarks>
/// This is the only place where the PC-side layout becomes a protocol message,
/// and it copies field by field from <see cref="StreamDeckLayoutDto"/> - which
/// already excludes action configuration - so no future field can reach the
/// tablet by accident. Failure messages are looked up from a fixed table rather
/// than forwarded, because the executor's text may quote a path or a URL.
/// </remarks>
internal static class StreamDeckProtocolMapper
{
    public const string IconMimeType = "image/png";

    public static StreamDeckLayoutMessage CreateLayout(
        string revision,
        StreamDeckLayoutDto? layout) => new()
        {
            Revision = revision,
            Profile = layout is null
                ? null
                : new StreamDeckProfileMessage
                {
                    Id = layout.ProfileId,
                    Name = layout.ProfileName,
                },
            Pages = layout is null
                ? []
                : [.. layout.Pages.Select(CreatePage)],
        };

    public static StreamDeckInvokeResultMessage CreateSuccess(string buttonId) => new()
    {
        ButtonId = buttonId,
        Success = true,
    };

    public static StreamDeckInvokeResultMessage CreateFailure(
        string buttonId,
        string errorCode) => new()
        {
            ButtonId = buttonId,
            Success = false,
            ErrorCode = errorCode,
            Message = Describe(errorCode),
        };

    public static StreamDeckInvokeResultMessage CreateResult(
        string buttonId,
        StreamDeckActionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Success
            ? CreateSuccess(buttonId)
            : CreateFailure(
                buttonId,
                result.ErrorCode ?? StreamDeckErrorCodes.ExecutionFailed);
    }

    /// <summary>
    /// Safe wording for a failure code. Anything unrecognised falls back to a
    /// generic sentence instead of leaking whatever produced it.
    /// </summary>
    public static string Describe(string errorCode) => errorCode switch
    {
        StreamDeckProtocolErrorCodes.StaleLayout =>
            "A configuração mudou no PC; o painel foi atualizado.",
        StreamDeckProtocolErrorCodes.InvalidRequest =>
            "Pedido inválido.",
        StreamDeckErrorCodes.ButtonNotFound =>
            "Este botão não existe mais na configuração do PC.",
        StreamDeckErrorCodes.ButtonDisabled =>
            "O botão está desativado.",
        StreamDeckErrorCodes.ActionNotConfigured =>
            "A ação ainda não foi configurada.",
        StreamDeckErrorCodes.ApplicationNotFound =>
            "O aplicativo configurado não foi encontrado.",
        StreamDeckErrorCodes.InvalidUrl =>
            "A URL configurada é inválida.",
        StreamDeckErrorCodes.ShellNotAllowed =>
            "Esta configuração não é permitida pelo PanelOS.",
        StreamDeckErrorCodes.UnsupportedAction =>
            "Esta ação não é suportada por esta versão.",
        StreamDeckErrorCodes.ConfigurationUnavailable =>
            "A configuração do Stream Deck não pôde ser lida.",
        _ => "A ação falhou no PC.",
    };

    private static StreamDeckPageMessage CreatePage(StreamDeckPageDto page) => new()
    {
        Id = page.Id,
        Name = page.Name,
        Rows = page.Rows,
        Columns = page.Columns,
        Buttons = [.. page.Buttons.Select(CreateButton)],
    };

    private static StreamDeckButtonMessage CreateButton(
        StreamDeckButtonDto button) => new()
        {
            Id = button.Id,
            Row = button.Row,
            Column = button.Column,
            Label = button.Label,
            IconId = button.Icon.Kind == StreamDeckIconReferenceKind.Image
                ? button.Icon.IconId
                : null,
            Glyph = button.Icon.Kind == StreamDeckIconReferenceKind.Glyph
                ? button.Icon.Glyph
                : null,
            Enabled = button.Enabled,
        };
}
