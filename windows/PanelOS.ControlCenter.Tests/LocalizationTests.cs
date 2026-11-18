using System.Collections;
using System.Globalization;
using System.Resources;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.ControlCenter.Resources;

namespace PanelOS.ControlCenter.Tests;

/// <summary>
/// The resource file is the only place user-visible text lives, in C# and in
/// XAML alike. These cases guard the properties that make that workable: every
/// entry says something, and a key that does not exist fails loudly rather than
/// rendering a blank label.
/// </summary>
[TestClass]
public sealed class LocalizationTests
{
    private static readonly ResourceManager Manager = new(
        "PanelOS.ControlCenter.Resources.ControlCenterStrings",
        typeof(LocExtension).Assembly);

    [TestMethod]
    public void EveryEntryHasText()
    {
        var empty = Entries()
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value))
            .Select(entry => entry.Key)
            .ToArray();

        Assert.AreEqual(
            0,
            empty.Length,
            $"recursos sem texto: {string.Join(", ", empty)}");
    }

    [TestMethod]
    public void TheResourceSetIsNotEmpty()
    {
        // A resx that failed to embed would otherwise only surface as blank
        // labels in a running window.
        Assert.IsTrue(Entries().Count > 100);
    }

    [TestMethod]
    public void AMissingKeyFailsLoudly()
    {
        Assert.ThrowsException<MissingManifestResourceException>(
            () => new LocExtension("NaoExisteEsteRecurso").ProvideValue(null!));
    }

    [TestMethod]
    public void TheMarkupExtensionResolvesTheSameTextAsTheCodePath()
    {
        Assert.AreEqual(
            Manager.GetString("AppTitle", CultureInfo.CurrentUICulture),
            new LocExtension("AppTitle").ProvideValue(null!));
    }

    [TestMethod]
    public void AnExtensionWithoutAKeyIsARefusalRatherThanABlankLabel()
    {
        Assert.ThrowsException<InvalidOperationException>(
            () => new LocExtension().ProvideValue(null!));
    }

    /// <summary>
    /// Format entries are called with a fixed argument count from one place
    /// each, so a stray brace is the failure that would survive review.
    /// </summary>
    [TestMethod]
    public void EveryPlaceholderIsBalanced()
    {
        var broken = Entries()
            .Where(entry =>
                entry.Value.Count(character => character == '{') !=
                entry.Value.Count(character => character == '}'))
            .Select(entry => entry.Key)
            .ToArray();

        Assert.AreEqual(
            0,
            broken.Length,
            $"placeholders desbalanceados: {string.Join(", ", broken)}");
    }

    private static IReadOnlyList<KeyValuePair<string, string>> Entries()
    {
        var set = Manager.GetResourceSet(
            CultureInfo.InvariantCulture,
            createIfNotExists: true,
            tryParents: true);
        Assert.IsNotNull(set);

        var entries = new List<KeyValuePair<string, string>>();
        foreach (DictionaryEntry entry in set!)
        {
            entries.Add(new KeyValuePair<string, string>(
                (string)entry.Key,
                entry.Value as string ?? string.Empty));
        }

        return entries;
    }
}
