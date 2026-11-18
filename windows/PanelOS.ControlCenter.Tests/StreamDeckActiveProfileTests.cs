using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.ControlCenter.ViewModels;
using PanelOS.Windows.StreamDeck;

namespace PanelOS.ControlCenter.Tests;

/// <summary>
/// Choosing which profile the tablet shows, the preview that follows it, and
/// the summary the Dashboard reports.
/// </summary>
[TestClass]
public sealed class StreamDeckActiveProfileTests
{
    [TestMethod]
    public async Task TheLoadedActiveProfileIsTheOneOpenedForEditing()
    {
        var store = new InMemoryStreamDeckConfigurationStore(TwoProfiles("profile-b"));
        var viewModel = new StreamDeckEditorViewModel(store);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.AreEqual("profile-b", viewModel.ActiveProfileId);
        Assert.AreEqual("Mídia", viewModel.ActiveProfileName);
        Assert.AreEqual("Mídia", viewModel.ProfileName);
        Assert.IsTrue(viewModel.IsSelectedProfileActive);
        Assert.IsFalse(viewModel.SetActiveProfileCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task BrowsingAnotherProfileDoesNotChangeWhatTheTabletShows()
    {
        var store = new InMemoryStreamDeckConfigurationStore(TwoProfiles("profile-a"));
        var viewModel = new StreamDeckEditorViewModel(store);
        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.SelectedProfile = viewModel.Profiles[1];

        // Looking at a profile is not the same decision as publishing it.
        Assert.AreEqual("profile-a", store.Configuration.ActiveProfileId);
        Assert.AreEqual("Principal", viewModel.ActiveProfileName);
        Assert.IsFalse(viewModel.IsSelectedProfileActive);
        Assert.IsTrue(viewModel.SetActiveProfileCommand.CanExecute(null));
        Assert.AreEqual(0, store.SaveCalls);
    }

    [TestMethod]
    public async Task UsingAProfileOnTheTabletPersistsImmediately()
    {
        var store = new InMemoryStreamDeckConfigurationStore(TwoProfiles("profile-a"));
        var viewModel = new StreamDeckEditorViewModel(store);
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.SelectedProfile = viewModel.Profiles[1];

        await viewModel.SetActiveProfileCommand.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(1, store.SaveCalls);
        Assert.AreEqual("profile-b", store.Saved!.ActiveProfileId);
        Assert.AreEqual("Mídia", viewModel.ActiveProfileName);
        Assert.IsTrue(viewModel.IsSelectedProfileActive);
        Assert.IsFalse(viewModel.SetActiveProfileCommand.CanExecute(null));
        StringAssert.Contains(viewModel.StatusMessage!, "Mídia");
    }

    [TestMethod]
    public async Task OnlyOneProfileIsEverActive()
    {
        var store = new InMemoryStreamDeckConfigurationStore(TwoProfiles("profile-a"));
        var viewModel = new StreamDeckEditorViewModel(store);
        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.SelectedProfile = viewModel.Profiles[1];
        await viewModel.SetActiveProfileCommand.ExecuteAsync(CancellationToken.None);
        viewModel.SelectedProfile = viewModel.Profiles[0];
        await viewModel.SetActiveProfileCommand.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual("profile-a", store.Saved!.ActiveProfileId);
        Assert.AreEqual(1, store.Saved.Profiles.Count(profile =>
            profile.Id == store.Saved.ActiveProfileId));
    }

    [TestMethod]
    public async Task DeletingTheActiveProfileMovesItRatherThanDanglingIt()
    {
        var store = new InMemoryStreamDeckConfigurationStore(TwoProfiles("profile-b"));
        var viewModel = new StreamDeckEditorViewModel(
            store,
            new StreamDeckEditorServices
            {
                Confirmation = new FakeConfirmationService(),
            });
        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.DeleteProfileCommand.Execute(null);

        Assert.AreEqual("profile-a", store.Configuration.ActiveProfileId);
        Assert.IsTrue(
            StreamDeckValidator.Validate(store.Configuration).IsValid,
            "an active profile that no longer exists would fail validation");
    }

    [TestMethod]
    public async Task SelectingAPageRebuildsThePreviewGrid()
    {
        var store = new InMemoryStreamDeckConfigurationStore(TwoProfiles("profile-a"));
        var viewModel = new StreamDeckEditorViewModel(store);
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.AddPageCommand.Execute(null);
        viewModel.SelectedPage = viewModel.Pages[0];

        Assert.AreEqual(2, viewModel.Pages.Count);
        Assert.AreEqual("Principal", viewModel.PageName);
        Assert.AreEqual(2, viewModel.Rows);
        Assert.AreEqual(2, viewModel.Columns);
        Assert.AreEqual(4, viewModel.Cells.Count);
        Assert.AreEqual("Play", viewModel.Cells[0].Label);

        viewModel.SelectedPage = viewModel.Pages[1];

        Assert.AreEqual("Nova página", viewModel.PageName);
        Assert.AreEqual(12, viewModel.Cells.Count);
        Assert.IsTrue(viewModel.Cells.All(cell => cell.IsEmpty));
    }

    [TestMethod]
    public async Task ThePreviewShowsExactlyWhatTheActiveProfilePublishes()
    {
        var store = new InMemoryStreamDeckConfigurationStore(TwoProfiles("profile-a"));
        var viewModel = new StreamDeckEditorViewModel(store);
        await viewModel.LoadAsync(CancellationToken.None);

        var layout = Windows.StreamDeck.Dto.StreamDeckLayoutProjector
            .Project(store.Configuration)!;

        Assert.AreEqual(layout.Pages[0].Rows, viewModel.Rows);
        Assert.AreEqual(layout.Pages[0].Columns, viewModel.Columns);
        CollectionAssert.AreEqual(
            layout.Pages[0].Buttons.Select(button => button.Label).ToArray(),
            viewModel.Cells
                .Where(cell => !cell.IsEmpty)
                .Select(cell => cell.Label)
                .ToArray());
    }

    [TestMethod]
    public async Task TheSummaryCountsTheActiveProfileAndFollowsEverySave()
    {
        var store = new InMemoryStreamDeckConfigurationStore(TwoProfiles("profile-a"));
        var viewModel = new StreamDeckEditorViewModel(store);
        var summaries = new List<StreamDeckSummary>();
        viewModel.SummaryChanged += (_, _) => summaries.Add(viewModel.Summary);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.AreEqual(1, summaries.Count);
        Assert.AreEqual("Principal", viewModel.Summary.ProfileName);
        Assert.AreEqual(1, viewModel.Summary.PageCount);
        Assert.AreEqual(1, viewModel.Summary.ActionCount);
        StringAssert.Contains(viewModel.Summary.Describe(), "Principal");

        viewModel.SelectedProfile = viewModel.Profiles[1];
        await viewModel.SetActiveProfileCommand.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(2, summaries.Count);
        Assert.AreEqual("Mídia", viewModel.Summary.ProfileName);
    }

    [TestMethod]
    public async Task SavingAnEditChangesTheRevisionAndTheSummary()
    {
        var store = new InMemoryStreamDeckConfigurationStore(TwoProfiles("profile-a"));
        var viewModel = new StreamDeckEditorViewModel(store);
        await viewModel.LoadAsync(CancellationToken.None);
        var before = viewModel.Summary.Revision;

        viewModel.SelectedCell = viewModel.Cells[0];
        viewModel.ButtonEditor!.Label = "Renomeado";
        await viewModel.SaveCommand.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(1, store.SaveCalls);
        Assert.AreNotEqual(before, viewModel.Summary.Revision);
        Assert.AreEqual(viewModel.LayoutRevision, viewModel.Summary.Revision);
        Assert.AreEqual(
            "Renomeado",
            store.Saved!.Profiles[0].Pages[0].Buttons[0].Label);
    }

    [TestMethod]
    public void TheDashboardReportsWhatTheEditorPersisted()
    {
        var dashboard = new DashboardViewModel(
            new FakePanelDriverController(),
            new FakePanelHostProcessManager());

        Assert.AreEqual("Sem perfil", dashboard.StreamDeckStatusText);
        StringAssert.Contains(dashboard.StreamDeckDetail, "Nenhum perfil");

        dashboard.ApplyStreamDeckSummary(
            new StreamDeckSummary("Principal", 3, 10, "abcdef0123456789"));

        Assert.AreEqual("Configurado", dashboard.StreamDeckStatusText);
        Assert.AreEqual(PanelStatusLevel.Good, dashboard.StreamDeckLevel);
        Assert.AreEqual(
            "Principal • 3 páginas • 10 ações",
            dashboard.StreamDeckDetail);
        StringAssert.Contains(dashboard.StreamDeckRevisionText, "abcdef0123456789");
    }

    private static StreamDeckConfiguration TwoProfiles(string activeProfileId) => new()
    {
        ActiveProfileId = activeProfileId,
        Profiles =
        [
            new StreamDeckProfile
            {
                Id = "profile-a",
                Name = "Principal",
                Pages = [Page("page-a", "Principal", "btn-a", "Play")],
            },
            new StreamDeckProfile
            {
                Id = "profile-b",
                Name = "Mídia",
                Pages = [Page("page-b", "Mídia", "btn-b", "Mute")],
            },
        ],
    };

    private static StreamDeckPage Page(
        string pageId,
        string name,
        string buttonId,
        string label) => new()
        {
            Id = pageId,
            Name = name,
            Rows = 2,
            Columns = 2,
            Buttons =
            [
                new StreamDeckButton
                {
                    Id = buttonId,
                    Row = 0,
                    Column = 0,
                    Label = label,
                    Action = new MediaCommandAction { Command = MediaCommand.PlayPause },
                },
            ],
        };
}
