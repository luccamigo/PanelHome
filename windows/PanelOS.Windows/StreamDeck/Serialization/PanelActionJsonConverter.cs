using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanelOS.Windows.StreamDeck.Serialization;

/// <summary>
/// Reads and writes <see cref="PanelAction"/> as a tagged object
/// (<c>{"kind": "...", ...}</c>). Written by hand so the persisted names are
/// part of the schema instead of a side effect of reflection.
/// </summary>
internal sealed class PanelActionJsonConverter : JsonConverter<PanelAction>
{
    private const string KindProperty = "kind";

    internal const string LaunchApplicationKind = "launchApplication";
    internal const string OpenUrlKind = "openUrl";
    internal const string KeyboardShortcutKind = "keyboardShortcut";
    internal const string MediaCommandKind = "mediaCommand";
    internal const string VolumeCommandKind = "volumeCommand";
    internal const string PanelModeKind = "panelModeCommand";

    public override PanelAction? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A Stream Deck action must be a JSON object.");
        }

        if (!root.TryGetProperty(KindProperty, out var kindElement) ||
            kindElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(
                "A Stream Deck action requires a string \"kind\".");
        }

        var kind = kindElement.GetString()!;
        return kind switch
        {
            LaunchApplicationKind => new LaunchApplicationAction
            {
                ExecutablePath = ReadOptionalString(root, "executablePath"),
                Arguments = ReadOptionalString(root, "arguments"),
                WorkingDirectory = ReadOptionalString(root, "workingDirectory"),
            },
            OpenUrlKind => new OpenUrlAction
            {
                Url = ReadOptionalString(root, "url"),
            },
            KeyboardShortcutKind => ReadKeyboardShortcut(root),
            MediaCommandKind => new MediaCommandAction
            {
                Command = ReadEnum(root, "command", MediaCommand.PlayPause),
            },
            VolumeCommandKind => new VolumeCommandAction
            {
                Command = ReadEnum(root, "command", VolumeCommand.MuteToggle),
                Level = ReadOptionalInt32(root, "level"),
            },
            PanelModeKind => new PanelModeAction
            {
                Mode = ReadEnum(root, "mode", PanelMode.Dashboard),
            },
            // Clone: the document is disposed with this call frame.
            _ => new UnknownPanelAction(kind, root.Clone()),
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        PanelAction value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        if (value is UnknownPanelAction unknown)
        {
            unknown.Payload.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        switch (value)
        {
            case LaunchApplicationAction launch:
                writer.WriteString(KindProperty, LaunchApplicationKind);
                WriteOptionalString(writer, "executablePath", launch.ExecutablePath);
                WriteOptionalString(writer, "arguments", launch.Arguments);
                WriteOptionalString(
                    writer,
                    "workingDirectory",
                    launch.WorkingDirectory);
                break;

            case OpenUrlAction openUrl:
                writer.WriteString(KindProperty, OpenUrlKind);
                WriteOptionalString(writer, "url", openUrl.Url);
                break;

            case KeyboardShortcutAction shortcut:
                writer.WriteString(KindProperty, KeyboardShortcutKind);
                writer.WriteStartArray("modifiers");
                foreach (var modifier in EnumerateModifiers(shortcut.Modifiers))
                {
                    writer.WriteStringValue(ToJsonName(modifier));
                }

                writer.WriteEndArray();
                writer.WriteString("key", ToJsonName(shortcut.Key));
                break;

            case MediaCommandAction media:
                writer.WriteString(KindProperty, MediaCommandKind);
                writer.WriteString("command", ToJsonName(media.Command));
                break;

            case VolumeCommandAction volume:
                writer.WriteString(KindProperty, VolumeCommandKind);
                writer.WriteString("command", ToJsonName(volume.Command));
                if (volume.Level is int level)
                {
                    writer.WriteNumber("level", level);
                }

                break;

            case PanelModeAction panelMode:
                writer.WriteString(KindProperty, PanelModeKind);
                writer.WriteString("mode", ToJsonName(panelMode.Mode));
                break;

            default:
                throw new JsonException(
                    $"Unsupported Stream Deck action type {value.GetType().Name}.");
        }

        writer.WriteEndObject();
    }

    internal static IEnumerable<PanelKeyModifiers> EnumerateModifiers(
        PanelKeyModifiers modifiers)
    {
        if (modifiers.HasFlag(PanelKeyModifiers.Control))
        {
            yield return PanelKeyModifiers.Control;
        }

        if (modifiers.HasFlag(PanelKeyModifiers.Shift))
        {
            yield return PanelKeyModifiers.Shift;
        }

        if (modifiers.HasFlag(PanelKeyModifiers.Alt))
        {
            yield return PanelKeyModifiers.Alt;
        }

        if (modifiers.HasFlag(PanelKeyModifiers.Windows))
        {
            yield return PanelKeyModifiers.Windows;
        }
    }

    private static KeyboardShortcutAction ReadKeyboardShortcut(JsonElement root)
    {
        var modifiers = PanelKeyModifiers.None;
        if (root.TryGetProperty("modifiers", out var modifiersElement) &&
            modifiersElement.ValueKind != JsonValueKind.Null)
        {
            if (modifiersElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException(
                    "\"modifiers\" must be an array of modifier names.");
            }

            foreach (var element in modifiersElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String ||
                    !Enum.TryParse<PanelKeyModifiers>(
                        element.GetString(),
                        ignoreCase: true,
                        out var modifier))
                {
                    throw new JsonException(
                        "\"modifiers\" contains an unknown modifier name.");
                }

                modifiers |= modifier;
            }
        }

        return new KeyboardShortcutAction
        {
            Modifiers = modifiers,
            Key = ReadEnum(root, "key", PanelKey.None),
        };
    }

    private static string? ReadOptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"\"{name}\" must be a string or null.");
        }

        return element.GetString();
    }

    private static int? ReadOptionalInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out var value))
        {
            throw new JsonException($"\"{name}\" must be an integer or null.");
        }

        return value;
    }

    private static TEnum ReadEnum<TEnum>(
        JsonElement root,
        string name,
        TEnum fallback)
        where TEnum : struct, Enum
    {
        if (!root.TryGetProperty(name, out var element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        if (element.ValueKind != JsonValueKind.String ||
            !Enum.TryParse<TEnum>(
                element.GetString(),
                ignoreCase: true,
                out var value) ||
            !Enum.IsDefined(value))
        {
            throw new JsonException(
                $"\"{name}\" is not a known {typeof(TEnum).Name} value.");
        }

        return value;
    }

    private static void WriteOptionalString(
        Utf8JsonWriter writer,
        string name,
        string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        writer.WriteString(name, value);
    }

    private static string ToJsonName<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
