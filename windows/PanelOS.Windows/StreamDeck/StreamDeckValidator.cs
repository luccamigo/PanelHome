namespace PanelOS.Windows.StreamDeck;

public sealed record StreamDeckValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static StreamDeckValidationResult Valid { get; } = new([]);
}

/// <summary>
/// Structural rules every persisted configuration must satisfy. Button and
/// page identifiers are validated globally, not per page: the tablet addresses
/// a button by its id alone, so a duplicate would make an invocation ambiguous.
/// </summary>
public static class StreamDeckValidator
{
    // A 1xN strip is not a deck, and past 6x6 the buttons stop being reachable
    // on a tablet-sized panel. Both ends are enforced when saving, not only in
    // the editor.
    public const int MinimumRows = 2;
    public const int MaximumRows = 6;
    public const int MinimumColumns = 2;
    public const int MaximumColumns = 6;
    public const int MaximumProfiles = 16;
    public const int MaximumPagesPerProfile = 16;
    public const int MaximumButtons = 1024;
    public const int MaximumIdentifierLength = 128;
    public const int MaximumNameLength = 80;
    public const int MaximumLabelLength = 80;
    public const int MaximumIconCount = 256;
    public const int MaximumActionStringLength = 2048;
    public const int MaximumConfigurationBytes = 1024 * 1024;

    public static StreamDeckValidationResult Validate(
        StreamDeckConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var errors = new List<string>();

        if (configuration.Version != StreamDeckConfiguration.CurrentSchemaVersion)
        {
            errors.Add(
                $"Schema v{configuration.Version} não é suportado " +
                $"(esperado v{StreamDeckConfiguration.CurrentSchemaVersion}).");
        }

        if (configuration.Profiles.Count == 0)
        {
            errors.Add("A configuração precisa de pelo menos um profile.");
        }
        else if (configuration.Profiles.Count > MaximumProfiles)
        {
            errors.Add($"A configuraÃ§Ã£o excede {MaximumProfiles} profiles.");
        }

        var profileIds = new HashSet<string>(StringComparer.Ordinal);
        var pageIds = new HashSet<string>(StringComparer.Ordinal);
        var buttonIds = new HashSet<string>(StringComparer.Ordinal);
        var iconIds = new HashSet<string>(StringComparer.Ordinal);
        var totalButtons = 0;

        foreach (var profile in configuration.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                errors.Add("Todo profile precisa de um id.");
            }
            else if (profile.Id.Length > MaximumIdentifierLength)
            {
                errors.Add($"Profile id excede {MaximumIdentifierLength} caracteres.");
            }
            else if (!profileIds.Add(profile.Id))
            {
                errors.Add($"Profile id duplicado: {profile.Id}.");
            }

            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                errors.Add($"Profile {profile.Id} precisa de um nome.");
            }
            else if (profile.Name.Length > MaximumNameLength)
            {
                errors.Add($"Profile {profile.Id} excede o limite de nome.");
            }

            if (profile.Pages.Count == 0)
            {
                errors.Add($"Profile {profile.Id} precisa de pelo menos uma página.");
            }
            else if (profile.Pages.Count > MaximumPagesPerProfile)
            {
                errors.Add($"Profile {profile.Id} excede {MaximumPagesPerProfile} pÃ¡ginas.");
            }

            foreach (var page in profile.Pages)
            {
                totalButtons += page.Buttons.Count;
                ValidatePage(page, pageIds, buttonIds, iconIds, errors);
            }
        }

        if (totalButtons > MaximumButtons)
        {
            errors.Add($"A configuraÃ§Ã£o excede {MaximumButtons} botÃµes.");
        }
        if (iconIds.Count > MaximumIconCount)
        {
            errors.Add($"A configuraÃ§Ã£o excede {MaximumIconCount} Ã­cones.");
        }

        if (configuration.ActiveProfileId is not null &&
            !profileIds.Contains(configuration.ActiveProfileId))
        {
            errors.Add(
                $"activeProfileId {configuration.ActiveProfileId} não existe.");
        }

        return errors.Count == 0
            ? StreamDeckValidationResult.Valid
            : new StreamDeckValidationResult(errors);
    }

    public static bool IsGridSizeValid(int rows, int columns) =>
        rows is >= MinimumRows and <= MaximumRows &&
        columns is >= MinimumColumns and <= MaximumColumns;

    private static void ValidatePage(
        StreamDeckPage page,
        HashSet<string> pageIds,
        HashSet<string> buttonIds,
        HashSet<string> iconIds,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(page.Id))
        {
            errors.Add("Toda página precisa de um id.");
        }
        else if (!pageIds.Add(page.Id))
        {
            errors.Add($"Página id duplicado: {page.Id}.");
        }
        else if (page.Id.Length > MaximumIdentifierLength)
        {
            errors.Add($"PÃ¡gina id excede {MaximumIdentifierLength} caracteres.");
        }

        if (string.IsNullOrWhiteSpace(page.Name))
        {
            errors.Add($"Página {page.Id} precisa de um nome.");
        }
        else if (page.Name.Length > MaximumNameLength)
        {
            errors.Add($"PÃ¡gina {page.Id} excede o limite de nome.");
        }

        if (page.Buttons.Count > page.Rows * page.Columns ||
            page.Buttons.Count > MaximumRows * MaximumColumns)
        {
            errors.Add($"PÃ¡gina {page.Id} tem botÃµes demais.");
        }

        if (!IsGridSizeValid(page.Rows, page.Columns))
        {
            errors.Add(
                $"Página {page.Id} tem grid {page.Rows}×{page.Columns}; " +
                $"o intervalo suportado é {MinimumRows}–{MaximumRows} linhas " +
                $"por {MinimumColumns}–{MaximumColumns} colunas.");
        }

        var occupied = new HashSet<(int Row, int Column)>();
        foreach (var button in page.Buttons)
        {
            if (string.IsNullOrWhiteSpace(button.Id))
            {
                errors.Add($"Todo botão da página {page.Id} precisa de um id.");
            }
            else if (!buttonIds.Add(button.Id))
            {
                errors.Add($"Botão id duplicado: {button.Id}.");
            }
            else if (button.Id.Length > MaximumIdentifierLength)
            {
                errors.Add($"BotÃ£o id excede {MaximumIdentifierLength} caracteres.");
            }

            if (string.IsNullOrWhiteSpace(button.Label))
            {
                errors.Add($"Botão {button.Id} precisa de um rótulo.");
            }
            else if (button.Label.Length > MaximumLabelLength)
            {
                errors.Add($"BotÃ£o {button.Id} excede o limite de rÃ³tulo.");
            }

            if (button.Row < 0 || button.Row >= page.Rows ||
                button.Column < 0 || button.Column >= page.Columns)
            {
                errors.Add(
                    $"Botão {button.Id} está em ({button.Row}, {button.Column}), " +
                    $"fora do grid {page.Rows}×{page.Columns}.");
            }
            else if (!occupied.Add((button.Row, button.Column)))
            {
                errors.Add(
                    $"A posição ({button.Row}, {button.Column}) da página " +
                    $"{page.Id} tem mais de um botão.");
            }

            if (button.Icon.Kind == StreamDeckIconKind.Custom &&
                string.IsNullOrWhiteSpace(button.Icon.IconId) &&
                string.IsNullOrWhiteSpace(button.Icon.SourcePath))
            {
                errors.Add($"Botão {button.Id} usa um ícone próprio sem imagem.");
            }

            if (!string.IsNullOrWhiteSpace(button.Icon.IconId) &&
                !Icons.StreamDeckIconCache.IsValidIconId(button.Icon.IconId))
            {
                errors.Add($"Botão {button.Id} tem um iconId inválido.");
            }
            else if (!string.IsNullOrWhiteSpace(button.Icon.IconId))
            {
                iconIds.Add(button.Icon.IconId);
            }

            ValidateActionStrings(button, errors);
        }
    }

    private static void ValidateActionStrings(StreamDeckButton button, List<string> errors)
    {
        IEnumerable<string?> values = button.Action switch
        {
            LaunchApplicationAction action =>
                [action.ExecutablePath, action.Arguments, action.WorkingDirectory],
            OpenUrlAction action => [action.Url],
            _ => [],
        };
        if (values.Any(value => value?.Length > MaximumActionStringLength))
        {
            errors.Add($"BotÃ£o {button.Id} excede o limite de texto da aÃ§Ã£o.");
        }
        if (button.Icon.SourcePath?.Length > MaximumActionStringLength)
        {
            errors.Add($"BotÃ£o {button.Id} excede o limite de sourcePath.");
        }
    }
}
