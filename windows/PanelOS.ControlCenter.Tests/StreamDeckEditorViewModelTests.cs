using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.ControlCenter.Services;
using PanelOS.ControlCenter.ViewModels;
using PanelOS.Windows.StreamDeck;
using PanelOS.Windows.StreamDeck.Runtime;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class StreamDeckEditorViewModelTests
{
    // The default page is 3x4: media on the first row, volume and shortcuts on
    // the second, application placeholders on the third.
    private const int PlayPauseCell = 0;
    private const int PreviousCell = 1;
    private const int VolumeUpCell = 4;
    private const int ShortcutCell = 6;
    private const int ApplicationCell = 8;

    [TestMethod]
    public async Task LoadBuildsTheGridFromTheActiveProfile()
    {
        var (viewModel, _, _) = await CreateLoadedAsync();

        Assert.AreEqual("Main", viewModel.ProfileName);
        Assert.AreEqual("Principal", viewModel.PageName);
        Assert.AreEqual(1, viewModel.Profiles.Count);
        Assert.AreEqual(1, viewModel.Pages.Count);
        Assert.AreEqual(3, viewModel.Rows);
        Assert.AreEqual(4, viewModel.Columns);
        Assert.AreEqual(12, viewModel.Cells.Count);
        Assert.AreEqual("Play/Pause", viewModel.Cells[PlayPauseCell].Label);
        Assert.AreEqual("Win + D", viewModel.Cells[ShortcutCell].ActionSummary);
        Assert.IsFalse(viewModel.IsDirty);
        Assert.IsFalse(viewModel.IsReadOnly);
        Assert.IsFalse(viewModel.SaveCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task TheDefaultProfileWorksWithoutAnyConfiguredPath()
    {
        var (viewModel, _, _) = await CreateLoadedAsync();

        // Everything except the application placeholders is runnable as is.
        var runnable = viewModel.Cells
            .Where(cell => cell.Button?.IsRunnable == true)
            .Count();
        Assert.AreEqual(8, runnable);
    }

    [TestMethod]
    public async Task MoveRightSwapsTheTwoButtonsAndFollowsTheSelection()
    {
        var (viewModel, _, _) = await CreateLoadedAsync();
        viewModel.SelectedCell = viewModel.Cells[PlayPauseCell];

        viewModel.MoveRightCommand.Execute(null);

        Assert.AreEqual("Anterior", viewModel.Cells[PlayPauseCell].Label);
        Assert.AreEqual("Play/Pause", viewModel.Cells[PreviousCell].Label);
        Assert.AreEqual(0, viewModel.Cells[PreviousCell].Button!.Row);
        Assert.AreEqual(1, viewModel.Cells[PreviousCell].Button!.Column);
        Assert.AreSame(viewModel.Cells[PreviousCell], viewModel.SelectedCell);
        Assert.IsTrue(viewModel.IsDirty);
    }

    [TestMethod]
    public async Task MoveDownAndBackRestoresTheOriginalLayout()
    {
        var (viewModel, _, _) = await CreateLoadedAsync();
        viewModel.SelectedCell = viewModel.Cells[PlayPauseCell];

        viewModel.MoveDownCommand.Execute(null);
        viewModel.MoveUpCommand.Execute(null);

        Assert.AreEqual("Play/Pause", viewModel.Cells[PlayPauseCell].Label);
        Assert.AreEqual("Volume +", viewModel.Cells[VolumeUpCell].Label);
    }

    [TestMethod]
    public async Task MovesAreRefusedOutsideTheGrid()
    {
        var (viewModel, _, _) = await CreateLoadedAsync();
        viewModel.SelectedCell = viewModel.Cells[PlayPauseCell];

        Assert.IsFalse(viewModel.MoveLeftCommand.CanExecute(null));
        Assert.IsFalse(viewModel.MoveUpCommand.CanExecute(null));
        Assert.IsTrue(viewModel.MoveRightCommand.CanExecute(null));
        Assert.IsTrue(viewModel.MoveDownCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task AddingAndRemovingAButtonUpdatesThePage()
    {
        var (viewModel, store, _) = await CreateLoadedAsync();
        var page = store.Configuration.Profiles[0].Pages[0];
        viewModel.SelectedCell = viewModel.Cells[PlayPauseCell];

        viewModel.RemoveButtonCommand.Execute(null);

        Assert.AreEqual(11, page.Buttons.Count);
        Assert.IsTrue(viewModel.Cells[PlayPauseCell].IsEmpty);
        Assert.IsNull(viewModel.ButtonEditor);
        Assert.IsTrue(viewModel.AddButtonCommand.CanExecute(null));

        viewModel.AddButtonCommand.Execute(null);

        Assert.AreEqual(12, page.Buttons.Count);
        Assert.AreEqual("Novo botão", viewModel.Cells[PlayPauseCell].Label);
        Assert.IsNotNull(viewModel.ButtonEditor);
        Assert.IsTrue(
            StreamDeckValidator.Validate(store.Configuration).IsValid,
            "a new button must not break the configuration");
    }

    [TestMethod]
    public async Task GrowingTheGridRebuildsTheCells()
    {
        var (viewModel, store, _) = await CreateLoadedAsync();

        viewModel.Rows = 4;

        Assert.AreEqual(4, viewModel.Rows);
        Assert.AreEqual(16, viewModel.Cells.Count);
        Assert.AreEqual(4, store.Configuration.Profiles[0].Pages[0].Rows);
        Assert.IsTrue(viewModel.IsDirty);
    }

    [TestMethod]
    public async Task ShrinkingTheGridIsRefusedWhileButtonsWouldFallOutside()
    {
        var (viewModel, store, _) = await CreateLoadedAsync();

        viewModel.Rows = 2;

        Assert.AreEqual(3, viewModel.Rows);
        Assert.AreEqual(3, store.Configuration.Profiles[0].Pages[0].Rows);
        Assert.AreEqual(12, viewModel.Cells.Count);
        StringAssert.Contains(viewModel.StatusMessage!, "fora do novo grid");
    }

    [TestMethod]
    public async Task OutOfRangeGridSizeIsRefused()
    {
        var (viewModel, _, _) = await CreateLoadedAsync();

        viewModel.Columns = 9;

        Assert.AreEqual(4, viewModel.Columns);
        StringAssert.Contains(viewModel.StatusMessage!, "colunas");
    }

    [TestMethod]
    public async Task EditingALabelMarksTheEditorDirtyAndUpdatesTheCell()
    {
        var (viewModel, _, _) = await CreateLoadedAsync();
        viewModel.SelectedCell = viewModel.Cells[PlayPauseCell];

        viewModel.ButtonEditor!.Label = "Editor";

        Assert.AreEqual("Editor", viewModel.Cells[PlayPauseCell].Label);
        Assert.IsTrue(viewModel.IsDirty);
        Assert.IsTrue(viewModel.SaveCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task DisablingAButtonIsReflectedInTheGrid()
    {
        var (viewModel, _, _) = await CreateLoadedAsync();
        viewModel.SelectedCell = viewModel.Cells[PlayPauseCell];

        viewModel.ButtonEditor!.Enabled = false;

        Assert.IsTrue(viewModel.Cells[PlayPauseCell].IsDisabled);
        Assert.IsFalse(viewModel.Cells[PlayPauseCell].Button!.IsRunnable);
        Assert.IsTrue(viewModel.IsDirty);
    }

    [TestMethod]
    public async Task ChangingTheActionKindReplacesTheAction()
    {
        var (viewModel, _, _) = await CreateLoadedAsync();
        viewModel.SelectedCell = viewModel.Cells[PlayPauseCell];
        var editor = viewModel.ButtonEditor!;

        editor.SelectedActionKind = editor.ActionKinds.First(
            option => option.Kind == PanelActionKind.OpenUrl);
        editor.Url = "https://panelos.invalid";

        Assert.IsTrue(editor.IsOpenUrl);
        Assert.IsFalse(editor.IsLaunchApplication);
        Assert.IsFalse(editor.IsMediaCommand);
        var action = viewModel.Cells[PlayPauseCell].Button!.Action as OpenUrlAction;
        Assert.IsNotNull(action);
        Assert.AreEqual("https://panelos.invalid", action.Url);
        Assert.AreEqual(
            "https://panelos.invalid",
            viewModel.Cells[PlayPauseCell].ActionSummary);
    }

    [TestMethod]
    public async Task ShortcutModifiersAreEditedAsStructuredFlags()
    {
        var (viewModel, _, _) = await CreateLoadedAsync();
        viewModel.SelectedCell = viewModel.Cells[ShortcutCell];
        var editor = viewModel.ButtonEditor!;

        Assert.IsTrue(editor.IsKeyboardShortcut);
        Assert.IsTrue(editor.ModifierWindows);
        Assert.AreEqual(PanelKey.D, editor.ShortcutKey);

        editor.ModifierControl = true;
        editor.ShortcutKey = PanelKey.F5;

        var action = (KeyboardShortcutAction)
            viewModel.Cells[ShortcutCell].Button!.Action!;
        Assert.AreEqual(
            PanelKeyModifiers.Control | PanelKeyModifiers.Windows,
            action.Modifiers);
        Assert.AreEqual(PanelKey.F5, action.Key);
        Assert.AreEqual("Ctrl + Win + F5", editor.ShortcutPreview);
    }

    [TestMethod]
    public async Task PickingAnApplicationFillsThePathAndTheEmptyLabel()
    {
        var picker = new FakeApplicationPicker
        {
            Result = new ApplicationPick(@"C:\Apps\Editor\editor.exe", "Editor"),
        };
        var icons = new FakeIconImporter();
        var (viewModel, _, _) = await CreateLoadedAsync(new StreamDeckEditorServices
        {
            ApplicationPicker = picker,
            IconImporter = icons,
        });
        viewModel.SelectedCell = viewModel.Cells[ApplicationCell];
        var editor = viewModel.ButtonEditor!;
        editor.Label = string.Empty;

        editor.BrowseApplicationCommand.Execute(null);

        Assert.AreEqual(1, picker.Calls);
        Assert.AreEqual(@"C:\Apps\Editor\editor.exe", editor.ExecutablePath);
        Assert.AreEqual("Editor", editor.Label);
        Assert.IsTrue(viewModel.IsDirty);
    }

    [TestMethod]
    public async Task PickingAnApplicationKeepsALabelTheUserChose()
    {
        var picker = new FakeApplicationPicker
        {
            Result = new ApplicationPick(@"C:\Apps\Editor\editor.exe", "Editor"),
        };
        var (viewModel, _, _) = await CreateLoadedAsync(new StreamDeckEditorServices
        {
            ApplicationPicker = picker,
            IconImporter = new FakeIconImporter(),
        });
        viewModel.SelectedCell = viewModel.Cells[ApplicationCell];
        var editor = viewModel.ButtonEditor!;
        editor.Label = "Meu editor";

        editor.BrowseApplicationCommand.Execute(null);

        Assert.AreEqual("Meu editor", editor.Label);
    }

    [TestMethod]
    public async Task PickingAnApplicationImportsItsIcon()
    {
        var icons = new FakeIconImporter { ExecutableIconId = "icon-app001" };
        var (viewModel, _, _) = await CreateLoadedAsync(new StreamDeckEditorServices
        {
            ApplicationPicker = new FakeApplicationPicker
            {
                Result = new ApplicationPick(@"C:\Apps\Editor\editor.exe", "Editor"),
            },
            IconImporter = icons,
        });
        viewModel.SelectedCell = viewModel.Cells[ApplicationCell];
        var editor = viewModel.ButtonEditor!;

        editor.BrowseApplicationCommand.Execute(null);

        Assert.AreEqual(@"C:\Apps\Editor\editor.exe", icons.LastImportedExecutable);
        var icon = viewModel.Cells[ApplicationCell].Button!.Icon;
        Assert.AreEqual(StreamDeckIconKind.Custom, icon.Kind);
        Assert.AreEqual("icon-app001", icon.IconId);
        Assert.AreEqual(@"C:\Apps\Editor\editor.exe", icon.SourcePath);
        Assert.IsTrue(editor.IsCustomIcon);
    }

    [TestMethod]
    public async Task ChoosingAnImageStoresOnlyTheCacheId()
    {
        var icons = new FakeIconImporter { ImageIconId = "icon-img002" };
        var (viewModel, _, _) = await CreateLoadedAsync(new StreamDeckEditorServices
        {
            IconPicker = new FakeIconFilePicker { Result = @"C:\Fotos\botao.png" },
            IconImporter = icons,
        });
        viewModel.SelectedCell = viewModel.Cells[PlayPauseCell];
        var editor = viewModel.ButtonEditor!;

        await editor.BrowseIconCommand.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(@"C:\Fotos\botao.png", icons.LastImportedImage);
        Assert.AreEqual("icon-img002", viewModel.Cells[PlayPauseCell].Button!.Icon.IconId);
        Assert.IsNotNull(viewModel.Cells[PlayPauseCell].IconPath);
    }

    [TestMethod]
    public async Task TestActionRunsTheSelectedButtonAndReportsSuccess()
    {
        var executor = new FakeActionExecutor();
        var (viewModel, _, _) = await CreateLoadedAsync(new StreamDeckEditorServices
        {
            ActionExecutor = executor,
        });
        viewModel.SelectedCell = viewModel.Cells[PlayPauseCell];

        await viewModel.TestActionCommand.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(1, executor.Calls);
        Assert.AreSame(
            viewModel.Cells[PlayPauseCell].Button,
            executor.LastButton);
        Assert.AreEqual("Executado", viewModel.TestStatusMessage);
        Assert.AreEqual(PanelStatusLevel.Good, viewModel.TestStatusLevel);
    }

    [TestMethod]
    public async Task TestActionShowsTheSpecificFailure()
    {
        var executor = new FakeActionExecutor
        {
            Result = StreamDeckActionResult.Error(
                StreamDeckErrorCodes.ApplicationNotFound,
                "Aplicativo não encontrado no caminho configurado."),
        };
        var (viewModel, _, _) = await CreateLoadedAsync(new StreamDeckEditorServices
        {
            ActionExecutor = executor,
        });
        viewModel.SelectedCell = viewModel.Cells[ApplicationCell];

        await viewModel.TestActionCommand.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(
            "Aplicativo não encontrado no caminho configurado.",
            viewModel.TestStatusMessage);
        Assert.AreEqual(PanelStatusLevel.Bad, viewModel.TestStatusLevel);
    }

    [TestMethod]
    public async Task TestActionNeedsAnExecutorAndASelectedButton()
    {
        var (withoutExecutor, _, _) = await CreateLoadedAsync();
        Assert.IsFalse(withoutExecutor.TestActionCommand.CanExecute(null));

        var (viewModel, _, _) = await CreateLoadedAsync(new StreamDeckEditorServices
        {
            ActionExecutor = new FakeActionExecutor(),
        });
        viewModel.SelectedCell = viewModel.Cells[PlayPauseCell];
        Assert.IsTrue(viewModel.TestActionCommand.CanExecute(null));

        viewModel.RemoveButtonCommand.Execute(null);
        Assert.IsFalse(viewModel.TestActionCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task AddingAProfileCreatesAnEmptyPageAndSelectsIt()
    {
        var (viewModel, store, _) = await CreateLoadedAsync();

        viewModel.AddProfileCommand.Execute(null);

        Assert.AreEqual(2, store.Configuration.Profiles.Count);
        Assert.AreEqual(2, viewModel.Profiles.Count);
        Assert.AreEqual("Novo perfil", viewModel.ProfileName);
        Assert.AreEqual(1, viewModel.Pages.Count);
        Assert.AreEqual(12, viewModel.Cells.Count);
        Assert.IsTrue(viewModel.Cells.All(cell => cell.IsEmpty));

        // Creating a profile does not hand the tablet an empty one: the active
        // profile only moves through "Usar no tablet".
        Assert.AreEqual(
            StreamDeckDefaults.MainProfileId,
            store.Configuration.ActiveProfileId);
        Assert.IsFalse(viewModel.IsSelectedProfileActive);
        Assert.IsTrue(viewModel.IsDirty);
    }

    [TestMethod]
    public async Task DuplicatingAProfileCopiesItWithFreshIdentifiers()
    {
        var (viewModel, store, _) = await CreateLoadedAsync();
        var original = viewModel.SelectedProfile!;

        viewModel.DuplicateProfileCommand.Execute(null);

        var copy = viewModel.SelectedProfile!;
        Assert.AreNotSame(original, copy);
        Assert.AreEqual("Main (cópia)", copy.Name);
        Assert.AreNotEqual(original.Id, copy.Id);
        Assert.AreNotEqual(original.Pages[0].Id, copy.Pages[0].Id);
        CollectionAssert.AreEqual(
            original.Pages[0].Buttons.Select(button => button.Label).ToArray(),
            copy.Pages[0].Buttons.Select(button => button.Label).ToArray());
        Assert.IsFalse(
            original.Pages[0].Buttons
                .Select(button => button.Id)
                .Intersect(copy.Pages[0].Buttons.Select(button => button.Id))
                .Any(),
            "os ids precisam ser únicos em toda a configuração");
        var validation = StreamDeckValidator.Validate(store.Configuration);
        Assert.IsTrue(validation.IsValid, string.Join(" ", validation.Errors));
    }

    [TestMethod]
    public async Task DeletingAProfileAsksFirst()
    {
        var confirmation = new FakeConfirmationService { Answer = false };
        var (viewModel, store, _) = await CreateLoadedAsync(
            new StreamDeckEditorServices { Confirmation = confirmation });
        viewModel.AddProfileCommand.Execute(null);

        viewModel.DeleteProfileCommand.Execute(null);

        Assert.AreEqual(1, confirmation.Calls);
        Assert.AreEqual(2, store.Configuration.Profiles.Count);

        confirmation.Answer = true;
        viewModel.DeleteProfileCommand.Execute(null);

        Assert.AreEqual(1, store.Configuration.Profiles.Count);
        Assert.AreEqual("Main", viewModel.ProfileName);
    }

    [TestMethod]
    public async Task TheLastProfileCannotBeDeleted()
    {
        var (viewModel, store, _) = await CreateLoadedAsync(
            new StreamDeckEditorServices
            {
                Confirmation = new FakeConfirmationService(),
            });

        Assert.IsFalse(viewModel.DeleteProfileCommand.CanExecute(null));
        viewModel.DeleteProfileCommand.Execute(null);
        Assert.AreEqual(1, store.Configuration.Profiles.Count);
    }

    [TestMethod]
    public async Task PagesCanBeAddedDuplicatedAndDeleted()
    {
        var confirmation = new FakeConfirmationService();
        var (viewModel, store, _) = await CreateLoadedAsync(
            new StreamDeckEditorServices { Confirmation = confirmation });
        var profile = store.Configuration.Profiles[0];

        viewModel.AddPageCommand.Execute(null);
        Assert.AreEqual(2, profile.Pages.Count);
        Assert.AreEqual("Nova página", viewModel.PageName);
        Assert.IsTrue(viewModel.Cells.All(cell => cell.IsEmpty));

        viewModel.SelectedPage = viewModel.Pages[0];
        viewModel.DuplicatePageCommand.Execute(null);
        Assert.AreEqual(3, profile.Pages.Count);
        Assert.AreEqual("Principal (cópia)", viewModel.PageName);
        Assert.AreEqual(12, viewModel.Cells.Count(cell => !cell.IsEmpty));

        viewModel.DeletePageCommand.Execute(null);
        Assert.AreEqual(2, profile.Pages.Count);
        Assert.AreEqual(1, confirmation.Calls);
        Assert.IsTrue(StreamDeckValidator.Validate(store.Configuration).IsValid);
    }

    [TestMethod]
    public async Task TheLastPageOfAProfileCannotBeDeleted()
    {
        var (viewModel, store, _) = await CreateLoadedAsync(
            new StreamDeckEditorServices
            {
                Confirmation = new FakeConfirmationService(),
            });

        Assert.IsFalse(viewModel.DeletePageCommand.CanExecute(null));
        viewModel.DeletePageCommand.Execute(null);
        Assert.AreEqual(1, store.Configuration.Profiles[0].Pages.Count);
    }

    [TestMethod]
    public async Task RenamingAProfileOrPageMarksTheEditorDirty()
    {
        var (viewModel, store, _) = await CreateLoadedAsync();

        viewModel.ProfileName = "Trabalho";
        viewModel.PageName = "Mídia";

        Assert.AreEqual("Trabalho", store.Configuration.Profiles[0].Name);
        Assert.AreEqual("Mídia", store.Configuration.Profiles[0].Pages[0].Name);
        Assert.AreSame(store.Configuration.Profiles[0], viewModel.SelectedProfile);
        Assert.AreSame(
            store.Configuration.Profiles[0].Pages[0],
            viewModel.SelectedPage);
        Assert.IsTrue(viewModel.IsDirty);
    }

    [TestMethod]
    public async Task SavePersistsTheEditedConfiguration()
    {
        var (viewModel, store, _) = await CreateLoadedAsync();
        viewModel.SelectedCell = viewModel.Cells[PlayPauseCell];
        viewModel.ButtonEditor!.Label = "Editor";

        await viewModel.SaveCommand.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(1, store.SaveCalls);
        Assert.AreEqual(
            "Editor",
            store.Saved!.Profiles[0].Pages[0].Buttons[0].Label);
        Assert.IsFalse(viewModel.IsDirty);
        StringAssert.Contains(viewModel.StatusMessage!, store.Location);
    }

    [TestMethod]
    public async Task NewerSchemaBlocksEditingInsteadOfOverwriting()
    {
        var store = new InMemoryStreamDeckConfigurationStore(
            source: StreamDeckConfigurationSource.UnsupportedSchema);
        var viewModel = new StreamDeckEditorViewModel(store);

        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.SelectedCell = viewModel.Cells[PlayPauseCell];

        Assert.IsTrue(viewModel.IsReadOnly);
        Assert.IsFalse(viewModel.CanEdit);
        Assert.IsFalse(viewModel.SaveCommand.CanExecute(null));
        Assert.IsFalse(viewModel.RemoveButtonCommand.CanExecute(null));
        Assert.IsFalse(viewModel.MoveRightCommand.CanExecute(null));
        Assert.IsFalse(viewModel.AddProfileCommand.CanExecute(null));
        Assert.IsFalse(viewModel.AddPageCommand.CanExecute(null));
        StringAssert.Contains(viewModel.StatusMessage!, "bloqueada");

        await viewModel.SaveCommand.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(0, store.SaveCalls);
    }

    [TestMethod]
    public async Task InvalidConfigurationIsReportedInsteadOfThrowing()
    {
        var (viewModel, store, _) = await CreateLoadedAsync();
        viewModel.SelectedCell = viewModel.Cells[PlayPauseCell];
        viewModel.ButtonEditor!.Label = string.Empty;

        await viewModel.SaveCommand.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(0, store.SaveCalls);
        StringAssert.Contains(viewModel.StatusMessage!, "rótulo");
        Assert.IsTrue(viewModel.IsDirty);
    }

    private static async Task<(
        StreamDeckEditorViewModel ViewModel,
        InMemoryStreamDeckConfigurationStore Store,
        StreamDeckEditorServices Services)> CreateLoadedAsync(
        StreamDeckEditorServices? services = null)
    {
        var store = new InMemoryStreamDeckConfigurationStore();
        services ??= StreamDeckEditorServices.None;
        var viewModel = new StreamDeckEditorViewModel(store, services);
        await viewModel.LoadAsync(CancellationToken.None);
        return (viewModel, store, services);
    }
}
