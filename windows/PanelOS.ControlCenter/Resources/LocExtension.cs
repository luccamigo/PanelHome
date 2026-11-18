using System.Windows.Markup;

namespace PanelOS.ControlCenter.Resources;

/// <summary>
/// Resolves user-visible text in XAML from
/// <see cref="ControlCenterStrings"/>: <c>{res:Loc OverviewTitle}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Without it a redesign quietly puts every label back into the markup, which
/// is the same problem the resource file was created to solve - only harder to
/// find, because a hardcoded string in XAML does not look like code.
/// </para>
/// <para>
/// A missing key throws at load time rather than rendering an empty label, and
/// the view smoke test loads every view, so a typo fails the build's test run
/// instead of shipping a blank line.
/// </para>
/// </remarks>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    /// <summary>Name of the entry in ControlCenterStrings.resx.</summary>
    [ConstructorArgument("key")]
    public string? Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        string.IsNullOrWhiteSpace(Key)
            ? throw new InvalidOperationException(
                "A chave do recurso é obrigatória em {res:Loc}.")
            : ControlCenterStrings.Get(Key);
}
