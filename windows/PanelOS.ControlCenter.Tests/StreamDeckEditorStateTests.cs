using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.ControlCenter.ViewModels;

namespace PanelOS.ControlCenter.Tests;

/// <summary>
/// The state the redesigned editor puts on screen: whether the file on disk
/// matches the editor, which slot the right column is configuring, and the
/// difference between the profile being edited and the one on the tablet.
/// </summary>
[TestClass]
public sealed class StreamDeckEditorStateTests
{
    [TestMethod]
    public async Task AFreshlyLoadedConfigurationReportsItselfAsSaved()
    {
        var viewModel = Create();

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.IsFalse(viewModel.IsDirty);
        Assert.AreEqual("Tudo salvo", viewModel.SaveStateText);
        Assert.AreEqual(PanelStatusLevel.Good, viewModel.SaveStateLevel);
    }

    [TestMethod]
    public async Task AnEditIsAnnouncedUntilItIsPersisted()
    {
        var viewModel = Create();
        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.PageName = "Mídia";

        // Only a saved configuration is one PanelHost can publish, so the
        // header has to distinguish the two states.
        Assert.IsTrue(viewModel.IsDirty);
        Assert.AreEqual("Alterações não salvas", viewModel.SaveStateText);
        Assert.AreEqual(PanelStatusLevel.Warning, viewModel.SaveStateLevel);

        await viewModel.SaveAsync(CancellationToken.None);

        Assert.AreEqual("Tudo salvo", viewModel.SaveStateText);
    }

    [TestMethod]
    public async Task AnEmptySlotOffersCreationAndAFilledOneOffersTheEditor()
    {
        var viewModel = Create();
        await viewModel.LoadAsync(CancellationToken.None);

        // The default profile fills its grid, so a slot is emptied first; that
        // is also the path a user takes to re-purpose a button.
        viewModel.SelectedCell = viewModel.Cells.First(cell => !cell.IsEmpty);
        viewModel.RemoveButtonCommand.Execute(null);

        Assert.IsTrue(viewModel.HasSelectedCell);
        Assert.IsTrue(viewModel.IsSelectedCellEmpty);
        Assert.IsFalse(viewModel.HasSelectedButton);
        Assert.IsTrue(viewModel.AddButtonCommand.CanExecute(null));

        viewModel.AddButtonCommand.Execute(null);

        Assert.IsFalse(viewModel.IsSelectedCellEmpty);
        Assert.IsTrue(viewModel.HasSelectedButton);
        Assert.IsFalse(viewModel.AddButtonCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task RemovingBringsTheSlotBackToItsEmptyState()
    {
        var viewModel = Create();
        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.SelectedCell = viewModel.Cells.First(cell => !cell.IsEmpty);
        Assert.IsFalse(viewModel.IsSelectedCellEmpty);

        viewModel.RemoveButtonCommand.Execute(null);

        Assert.IsTrue(viewModel.IsSelectedCellEmpty);
        Assert.IsFalse(viewModel.HasSelectedButton);
    }

    [TestMethod]
    public async Task BrowsingAnotherProfileNeverMovesTheOneOnTheTablet()
    {
        var store = new InMemoryStreamDeckConfigurationStore();
        var viewModel = new StreamDeckEditorViewModel(store);
        await viewModel.LoadAsync(CancellationToken.None);

        var activeBefore = viewModel.ActiveProfileId;
        viewModel.AddProfileCommand.Execute(null);

        // The editor is now on a different profile; the panel on the desk is
        // still showing the one it was showing.
        Assert.AreEqual("Novo perfil", viewModel.ProfileName);
        Assert.AreEqual(activeBefore, viewModel.ActiveProfileId);
        Assert.IsFalse(viewModel.IsSelectedProfileActive);
        Assert.IsTrue(viewModel.SetActiveProfileCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task TheProfileAlreadyOnTheTabletCannotBeSetAgain()
    {
        var viewModel = Create();
        await viewModel.LoadAsync(CancellationToken.None);

        Assert.IsTrue(viewModel.IsSelectedProfileActive);
        Assert.IsFalse(viewModel.SetActiveProfileCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task TheGridStaysWithinTheLimitsTheValidatorAccepts()
    {
        var viewModel = Create();
        await viewModel.LoadAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { 2, 3, 4, 5, 6 },
            viewModel.GridSizeOptions.ToArray());
    }

    private static StreamDeckEditorViewModel Create() =>
        new(new InMemoryStreamDeckConfigurationStore());
}
