using System.Globalization;
using PanelOS.ControlCenter.Mvvm;
using PanelOS.ControlCenter.Services;
using PanelOS.Windows.Management;

namespace PanelOS.ControlCenter.ViewModels;

/// <summary>Presentation only. User actions own every write; refresh only reads status.</summary>
public sealed class IntegrationsViewModel : ObservableObject
{
    private readonly ILocalIntegrationAdmin _admin;
    private readonly ClaudeHookInstaller _installer;
    private readonly IClipboardService _clipboard;
    private readonly IConfirmationService _confirmation;
    private bool _available, _enabled, _busy, _reviewed, _processEnabled, _hookInstalled, _dialogOpen;
    private string _state = "PanelHost parado", _detail = "O PanelHost não está rodando, então nada recebe os avisos.";
    private string _port = "8777", _keyLabel = "Não criada", _lastRequest = "—", _rejected = "0", _outcome = "", _hookStatus = "Consultando…";
    private string? _oneTimeKey;
    private string _dialogTitle = "", _previewText = "", _previewFiles = "", _dialogBody = "", _backup = "";
    private int _permissionTimeout = 60;
    private ClaudeHookInstallPreview? _installPreview;
    private ClaudeHookRemovalPreview? _removalPreview;
    private string _exampleKind = "curl";
    private long _keyGeneration;

    public IntegrationsViewModel(ILocalIntegrationAdmin admin, ClaudeHookInstaller installer,
        IClipboardService clipboard, IConfirmationService confirmation)
    {
        _admin = admin; _installer = installer; _clipboard = clipboard; _confirmation = confirmation;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        ToggleNoticesCommand = Command(ToggleNoticesAsync);
        RotateKeyCommand = Command(RotateKeyAsync);
        ApplyPortCommand = Command(ApplyPortAsync);
        TestNoticeCommand = Command(ct => SendAsync(new("test_notice"), ct), () => NoticesEnabled);
        TestPermissionCommand = Command(TestPermissionAsync);
        ApplyTimeoutCommand = Command(ct => SendAsync(new("configure", PermissionTimeoutSeconds: PermissionTimeout), ct));
        ToggleProcessesCommand = Command(ToggleProcessesAsync, () => ProcessReviewed);
        PreviewInstallCommand = new RelayCommand(PreviewInstall, () => !DialogOpen && !_hookInstalled);
        PreviewRemovalCommand = new RelayCommand(PreviewRemoval, () => !DialogOpen && _hookInstalled);
        ConfirmHookCommand = new RelayCommand(ConfirmHook, () => DialogOpen);
        CancelHookCommand = new RelayCommand(CancelHook);
        ViewHookCommand = new RelayCommand(ViewHook, () => _hookInstalled);
        CopyKeyCommand = new RelayCommand(() => { if (_oneTimeKey is { } key) _clipboard.SetText(key); }, () => HasOneTimeKey);
        DismissKeyCommand = new RelayCommand(ClearSensitive);
        CopyExampleCommand = new RelayCommand(() => _clipboard.SetText(Example));
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ToggleNoticesCommand { get; }
    public AsyncRelayCommand RotateKeyCommand { get; }
    public AsyncRelayCommand ApplyPortCommand { get; }
    public AsyncRelayCommand TestNoticeCommand { get; }
    public AsyncRelayCommand TestPermissionCommand { get; }
    public AsyncRelayCommand ApplyTimeoutCommand { get; }
    public AsyncRelayCommand ToggleProcessesCommand { get; }
    public RelayCommand PreviewInstallCommand { get; }
    public RelayCommand PreviewRemovalCommand { get; }
    public RelayCommand ConfirmHookCommand { get; }
    public RelayCommand CancelHookCommand { get; }
    public RelayCommand ViewHookCommand { get; }
    public RelayCommand CopyKeyCommand { get; }
    public RelayCommand DismissKeyCommand { get; }
    public RelayCommand CopyExampleCommand { get; }
    public bool NoticesEnabled { get => _enabled; private set => SetProperty(ref _enabled, value); }
    public bool Available { get => _available; private set => SetProperty(ref _available, value); }
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) RaiseCommands(); } }
    public bool ProcessReviewed { get => _reviewed; private set => SetProperty(ref _reviewed, value); }
    public bool ProcessesEnabled { get => _processEnabled; private set => SetProperty(ref _processEnabled, value); }
    public string ProcessReason => ProcessReviewed ? "O PC nunca encerra processos do sistema ou críticos e registra cada pedido no log." : "Aguardando revisão de segurança.";
    public string NoticeStatus { get => _state; private set => SetProperty(ref _state, value); }
    public string NoticeDetail { get => _detail; private set => SetProperty(ref _detail, value); }
    public PanelStatusLevel NoticeLevel => NoticeStatus switch
    {
        "Ouvindo" => PanelStatusLevel.Good,
        "Erro" => PanelStatusLevel.Bad,
        "Falta a chave" or "Indisponível" => PanelStatusLevel.Warning,
        _ => PanelStatusLevel.Neutral,
    };
    public string Port { get => _port; set { if (SetProperty(ref _port, value)) OnPropertyChanged(nameof(Example)); } }
    public string KeyLabel { get => _keyLabel; private set => SetProperty(ref _keyLabel, value); }
    public string LastRequest { get => _lastRequest; private set => SetProperty(ref _lastRequest, value); }
    public string RejectedToday { get => _rejected; private set => SetProperty(ref _rejected, value); }
    public string Outcome { get => _outcome; private set => SetProperty(ref _outcome, value); }
    public string HookStatus { get => _hookStatus; private set => SetProperty(ref _hookStatus, value); }
    public string Backup { get => _backup; private set => SetProperty(ref _backup, value); }
    public string? OneTimeKey { get => _oneTimeKey; private set { if (SetProperty(ref _oneTimeKey, value)) { OnPropertyChanged(nameof(HasOneTimeKey)); CopyKeyCommand.RaiseCanExecuteChanged(); } } }
    public bool HasOneTimeKey => OneTimeKey is not null;
    public bool DialogOpen { get => _dialogOpen; private set { if (SetProperty(ref _dialogOpen, value)) RaiseCommands(); } }
    public string DialogTitle { get => _dialogTitle; private set => SetProperty(ref _dialogTitle, value); }
    public string DialogBody { get => _dialogBody; private set => SetProperty(ref _dialogBody, value); }
    public string PreviewText { get => _previewText; private set => SetProperty(ref _previewText, value); }
    /// <summary>The stable folder the hook is copied to; shown in the preview before anything is written.</summary>
    public string InstallDirectory => _installer.InstallDirectory;
    /// <summary>One line per file: name, SHA-256, size. Empty outside the install preview.</summary>
    public string PreviewFilesText { get => _previewFiles; private set { if (SetProperty(ref _previewFiles, value)) OnPropertyChanged(nameof(HasPreviewFiles)); } }
    public bool HasPreviewFiles => _previewFiles.Length > 0;
    public string DialogAction => _installPreview is not null ? "Instalar" : _removalPreview is not null ? "Remover" : "Fechar";
    public IReadOnlyList<int> TimeoutOptions { get; } = [30, 60, 90, 120];
    public int PermissionTimeout { get => _permissionTimeout; set { if (TimeoutOptions.Contains(value)) SetProperty(ref _permissionTimeout, value); } }
    public IReadOnlyList<string> ExampleKinds { get; } = ["curl", "PowerShell", "Zelador"];
    public string ExampleKind { get => _exampleKind; set { if (SetProperty(ref _exampleKind, value)) OnPropertyChanged(nameof(Example)); } }
    public string Example
    {
        get
        {
            var port = int.TryParse(Port, out var parsed) && parsed is >= 1024 and <= 65535 ? parsed : 8777;
            var endpoint = $"http://127.0.0.1:{port}/v1/notices";
            return ExampleKind == "curl"
                ? $"curl -X POST {endpoint} -H \"Content-Type: application/json\" -H \"Authorization: Bearer <chave>\" -d \"{{\\\"title\\\":\\\"Build falhou\\\",\\\"text\\\":\\\"3 testes quebraram\\\"}}\""
                : $"Invoke-RestMethod -Method Post -Uri '{endpoint}' -Headers @{{ Authorization = 'Bearer <chave>' }} -ContentType 'application/json' -Body '{{\"title\":\"Build falhou\",\"text\":\"3 testes quebraram\"}}'";
        }
    }

    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!IsBusy && !DialogOpen) await RefreshAsync(token).ConfigureAwait(true);
            await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(true);
        }
    }

    public async Task RefreshAsync(CancellationToken token)
    {
        try { Apply(await _admin.SendAsync(new("status"), token).ConfigureAwait(true)); }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Available = false; NoticeStatus = "PanelHost parado";
            NoticeDetail = "O PanelHost não está rodando, então nada recebe os avisos.";
            ClearSensitive();
        }
        ReadHookStatus();
        RaiseCommands();
    }

    private AsyncRelayCommand Command(Func<CancellationToken, Task> action, Func<bool>? canExecute = null) =>
        new(async ct =>
        {
            IsBusy = true;
            try { await action(ct).ConfigureAwait(true); }
            catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { Outcome = "O PanelHost não confirmou a operação. Confira o estado antes de tentar de novo."; }
            finally { IsBusy = false; }
        }, () => !IsBusy && Available && (canExecute?.Invoke() ?? true));

    private async Task SendAsync(LocalIntegrationAdminRequest request, CancellationToken token)
    {
        var result = await _admin.SendAsync(request, token).ConfigureAwait(true);
        Apply(result);
        Outcome = result.Error switch
        {
            null => "Configuração recebida pelo PanelHost.",
            "review_required" => "Aguardando revisão de segurança.",
            "disabled" => "Os avisos estão desligados ou falta a chave.",
            "tablet_disconnected" => "O tablet não está conectado.",
            "invalid_port" => "Escolha uma porta entre 1024 e 65535.",
            _ => "O PanelHost recusou a operação. Confira a configuração.",
        };
        if (request.Action == "test_notice" && result.Error is null) Outcome = "Aviso enviado. Procure no tablet; se estiver desconectado, ele espera na fila.";
        if (request.Action == "rotate_key") OneTimeKey = result.OneTimeKey;
    }

    private Task ToggleNoticesAsync(CancellationToken token)
    {
        if (NoticesEnabled && !_confirmation.Confirm("Desligar os avisos?", "A chave atual deixa de funcionar. Ao ligar de novo, gere outra e atualize seus programas.")) return Task.CompletedTask;
        if (NoticesEnabled) ClearSensitive();
        return SendAsync(new("configure", Enabled: !NoticesEnabled), token);
    }
    private Task RotateKeyAsync(CancellationToken token)
    {
        if (KeyLabel != "Não criada" && !_confirmation.Confirm("Gerar uma nova chave?", "A chave atual deixa de funcionar na hora. Atualize todo programa que a usa.")) return Task.CompletedTask;
        ClearSensitive();
        return SendAsync(new("rotate_key"), token);
    }
    private Task ApplyPortAsync(CancellationToken token)
    {
        if (!int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
        { Outcome = "Escolha uma porta entre 1024 e 65535."; return Task.CompletedTask; }
        return SendAsync(new("configure", Port: port), token);
    }
    private Task ToggleProcessesAsync(CancellationToken token)
    {
        if (!ProcessReviewed) { Outcome = "Aguardando revisão de segurança."; return Task.CompletedTask; }
        if (!ProcessesEnabled && !_confirmation.Confirm("Permitir encerrar processos pelo tablet?", "Qualquer pessoa com acesso ao tablet poderá encerrar processos do seu usuário, segurando o botão. Processos do sistema continuam protegidos.")) return Task.CompletedTask;
        return SendAsync(new("processes.configure", Enabled: !ProcessesEnabled), token);
    }
    private async Task TestPermissionAsync(CancellationToken token)
    {
        Outcome = "Pedido enviado. Aguardando o tablet…";
        await SendAsync(new("test_permission"), token).ConfigureAwait(true);
        if (Outcome == "Configuração recebida pelo PanelHost.") Outcome = "Teste concluído. Confira a decisão no tablet e no estado do PanelHost.";
    }

    private void Apply(LocalIntegrationAdminResult result)
    {
        if (_keyGeneration != result.KeyGeneration) ClearSensitive();
        _keyGeneration = result.KeyGeneration;
        Available = true; NoticesEnabled = result.Enabled; Port = result.Port.ToString(CultureInfo.InvariantCulture);
        KeyLabel = result.KeySuffix is null ? "Não criada" : "panelos_••••••••••••" + result.KeySuffix;
        LastRequest = result.LastRequestUnixMs is long timestamp ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "—";
        RejectedToday = result.RejectedAuthenticationToday.ToString(CultureInfo.CurrentCulture);
        PermissionTimeout = result.PermissionTimeoutSeconds;
        ProcessReviewed = result.ProcessTerminationReviewed; ProcessesEnabled = result.ProcessTerminationEnabled;
        OnPropertyChanged(nameof(ProcessReason));
        (NoticeStatus, NoticeDetail) = result.State switch
        {
            "listening" => ("Ouvindo", "Aceitando pedidos feitos neste PC."),
            "disabled" => ("Desligado", "Os avisos estão desligados. Nada escuta neste PC."),
            "key_required" => ("Falta a chave", "Gere uma chave para começar. Sem chave, todo pedido é recusado."),
            "port_in_use" => ("Erro", $"A porta {result.Port} já está em uso. Escolha outra porta."),
            "requires_unelevated" => ("Indisponível", "Abra o PanelHost sem elevação para receber avisos."),
            _ => ("Indisponível", "Não consegui confirmar o estado dos avisos."),
        };
        OnPropertyChanged(nameof(NoticeLevel));
        RaiseCommands();
    }

    public void ClearSensitive() { OneTimeKey = null; }
    private void ReadHookStatus()
    {
        try
        {
            var status = _installer.Status(); _hookInstalled = status.Installed;
            HookStatus = !status.Installed ? "Desligado" : HookStatusText(status);
            Backup = status.BackupPath ?? "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { HookStatus = "Não consegui ler o estado do hook."; }
    }
    private static string HookStatusText(ClaudeHookStatus status)
    {
        var notes = new List<string>();
        if (status.Changed) notes.Add("arquivo de configurações alterado depois");
        if (status.HookFilesChanged) notes.Add("arquivo do hook alterado ou ausente");
        if (status.Outdated) notes.Add("desatualizado: remova e instale de novo para usar a versão atual");
        return notes.Count == 0 ? "Instalado" : "Instalado · " + string.Join(" · ", notes);
    }
    private static string Describe(ClaudeHookFilePreview file) => $"{file.Name}  {file.Sha256}  {file.Length} bytes";
    private void PreviewInstall()
    {
        try
        {
            _installPreview = _installer.Preview(); _removalPreview = null;
            DialogTitle = "Instalar o hook do PanelOS?";
            var folder = string.IsNullOrEmpty(_installPreview.InstallDirectory) ? _installer.InstallDirectory : _installPreview.InstallDirectory;
            DialogBody = $"Vou copiar os arquivos listados para {folder} e acrescentar uma entrada em {_installPreview.SettingsPath} que aponta para essa cópia, nunca para a pasta do build. Abaixo estão os arquivos com o SHA-256 e o texto exato que será gravado. Backup em {_installPreview.BackupPath}. O hook só fala com o PanelHost deste usuário por pipe local; sem chave ou HTTP. A cada pedido, o nome da ferramenta e da pasta do projeto, além do resumo do comando ou do caminho do arquivo, aparecem na tela do tablet. Evite usar com comandos que contenham senhas ou tokens.";
            PreviewFilesText = string.Join("\n", (_installPreview.Files ?? []).Select(Describe));
            PreviewText = _installPreview.Text; OpenDialog();
        }
        catch (FileNotFoundException)
        { Outcome = "Faltam arquivos do hook ao lado do Control Center. Nada foi gravado."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        { Outcome = "Não consegui preparar a instalação. Confira se as configurações são um JSON válido. Nada foi gravado."; }
    }
    private void PreviewRemoval()
    {
        try
        {
            _removalPreview = _installer.PreviewRemoval(); _installPreview = null; PreviewText = ""; PreviewFilesText = "";
            DialogTitle = _removalPreview.Mode == "restore_backup" ? "Remover o hook?" : "Remover só a entrada do PanelOS?";
            DialogBody = (_removalPreview.Mode == "restore_backup" ? "O arquivo volta exatamente ao backup anterior à instalação." :
                "O arquivo mudou ou o backup não existe mais. Vou retirar só o hook do PanelOS e manter o resto.") +
                $" As cópias do hook em {_installer.InstallDirectory} que ainda têm o hash instalado também são apagadas, e a pasta, se ficar vazia.";
            OpenDialog();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Outcome = "Não consegui preparar a remoção. Nada foi alterado."; }
    }
    private void ViewHook()
    {
        try
        {
            _installPreview = null; _removalPreview = null; PreviewFilesText = "";
            DialogTitle = "Configurações do hook"; DialogBody = _installer.SettingsPath;
            PreviewText = File.ReadAllText(_installer.SettingsPath); OpenDialog();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Outcome = "Não consegui ler o arquivo."; }
    }
    private void OpenDialog() { ClearSensitive(); DialogOpen = true; OnPropertyChanged(nameof(DialogAction)); }
    private void ConfirmHook()
    {
        try
        {
            if (_installPreview is { } install) { _installer.Install(install); Outcome = "Hook instalado. O backup foi guardado."; }
            else if (_removalPreview is { } removal) { _installer.Remove(removal); Outcome = "Hook removido. O Claude Code volta a perguntar só no terminal."; }
            CancelHook(); ReadHookStatus(); RaiseCommands();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        { Outcome = "O arquivo mudou ou não pôde ser gravado. Feche e confira uma nova prévia."; }
    }
    private void CancelHook() { DialogOpen = false; _installPreview = null; _removalPreview = null; PreviewText = ""; PreviewFilesText = ""; DialogBody = ""; }
    private void RaiseCommands()
    {
        foreach (var command in new[] { ToggleNoticesCommand, RotateKeyCommand, ApplyPortCommand, TestNoticeCommand,
            TestPermissionCommand, ApplyTimeoutCommand, ToggleProcessesCommand }) command.RaiseCanExecuteChanged();
        PreviewInstallCommand.RaiseCanExecuteChanged(); PreviewRemovalCommand.RaiseCanExecuteChanged();
        ConfirmHookCommand.RaiseCanExecuteChanged(); ViewHookCommand.RaiseCanExecuteChanged();
    }
}
