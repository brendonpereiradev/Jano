using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Vistora.Automation.Edge;
using Vistora.Core;
using Vistora.Infrastructure;

namespace Vistora.Desktop;

/// <summary>O que a tela principal está fazendo; define quais ações ficam disponíveis.</summary>
public enum ScreenState { Loading, Idle, Running, Stopping, Cleaning }

/// <summary>Cor de um texto de status; a janela a resolve com o pincel do tema atual (Tone.KeyFor).</summary>
public enum StatusTone { Text, Muted, Success, Attention, Danger }

public static class StatusTones
{
    public static StatusTone For(RunState state) => state switch
    {
        RunState.Completed => StatusTone.Success,
        RunState.Running => StatusTone.Text,
        RunState.Interrupted => StatusTone.Attention,
        _ => StatusTone.Danger
    };
}

/// <summary>Janelas que o ViewModel abre; a janela principal implementa, e os testes simulam as respostas.</summary>
public interface IMainDialogs
{
    bool ConfirmDanger(string title, string message, string confirmLabel);
    bool Confirm(string message);
    VisitProfile? EditProfile(VisitProfile profile);
    string? AskIssueKey(string floorName);
    bool ConfirmCloseRetry(FloorRun floor, IssueSnapshot snapshot);
    void ShowWarning(string message);
    void ShowError(string message);
    Task ExportLogAsync(VisitRun? run);
}

public sealed class RelayCommand(Action execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute();
    public void Execute(object? parameter) => execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Rótulo de um campo do Jira na tela de Configurações.</summary>
public sealed class FieldLabelItem(JiraField field, Action changed) : INotifyPropertyChanged
{
    public JiraField Field { get; } = field;
    public string Description => Field.Description;
    private string label = "";
    public string Label
    {
        get => label;
        set { if (label == value) return; label = value; PropertyChanged?.Invoke(this, new(nameof(Label))); changed(); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly LocalStore store;
    private readonly ExecutionEngine engine;
    private readonly DiagnosticService diagnostics;
    private readonly IMainDialogs dialogs;
    private readonly Func<LocalStore, IJiraAutomation> automationFactory;
    private readonly SynchronizationContext? ui;
    private readonly bool pendingPreview;
    private readonly RelayCommand[] commands;
    private ProfileDocument profiles = new();
    private AppSettings settings = new();
    private List<VisitRun> runs = [];
    private CancellationTokenSource? cancellation;
    private Task? activeTask;
    // Ligado enquanto o próprio ViewModel troca listas e seleções, para que essas trocas não pareçam escolhas do operador.
    private bool updatingSelection;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel(DiagnosticService diagnostics, IMainDialogs dialogs, bool preview = false, bool pendingPreview = false,
        Func<LocalStore, IJiraAutomation>? automationFactory = null)
    {
        this.diagnostics = diagnostics;
        this.dialogs = dialogs;
        IsPreview = preview;
        this.pendingPreview = pendingPreview;
        this.automationFactory = automationFactory ?? (s => new EdgeJiraAutomation(s));
        ui = SynchronizationContext.Current;
        store = new LocalStore(diagnostics.Root, diagnostics);
        engine = new ExecutionEngine(store, () => this.automationFactory(store), diagnostics);
        engine.Progress += run => OnUi(() => ShowProgress(run));
        diagnostics.WarningRaised += message => PostToUi(() => DiagnosticWarning = message);
        diagnosticWarning = diagnostics.Warning;
        ExecuteCommand = new(() => _ = ExecuteAsync(), CanExecuteVisit);
        DiscardCommand = new(() => _ = DiscardAsync(), () => IsUnlocked);
        CheckAccessCommand = new(() => _ = CheckAccessAsync(), () => State == ScreenState.Idle);
        StopCommand = new(Stop, () => State == ScreenState.Running);
        NewProfileCommand = new(() => _ = NewProfileAsync(), () => IsUnlocked);
        EditProfileCommand = new(() => _ = EditProfileAsync(), () => IsUnlocked);
        DeleteProfileCommand = new(() => _ = DeleteProfileAsync(), () => IsUnlocked);
        ExportLogCommand = new(() => _ = ExportLogAsync(), () => State != ScreenState.Cleaning && !IsExporting);
        ClearHistoryCommand = new(() => _ = ClearHistoryAsync(), () => State == ScreenState.Idle && !IsExporting && !IsPreview);
        SaveSettingsCommand = new(() => _ = SaveSettingsAsync(), () => IsUnlocked);
        FieldLabels = JiraFields.All.Select(f => new FieldLabelItem(f, () => SettingsStatus = "")).ToList();
        commands = [ExecuteCommand, DiscardCommand, CheckAccessCommand, StopCommand, NewProfileCommand, EditProfileCommand,
            DeleteProfileCommand, ExportLogCommand, ClearHistoryCommand, SaveSettingsCommand];
    }

    public LocalStore Store => store;
    public RelayCommand ExecuteCommand { get; }
    public RelayCommand DiscardCommand { get; }
    public RelayCommand CheckAccessCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand NewProfileCommand { get; }
    public RelayCommand EditProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand ExportLogCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }

    // Estado da tela

    private ScreenState state = ScreenState.Loading;
    public ScreenState State
    {
        get => state;
        internal set
        {
            if (!Set(ref state, value)) return;
            OnPropertyChanged(nameof(IsUnlocked)); OnPropertyChanged(nameof(IsStopVisible)); OnPropertyChanged(nameof(StopLabel));
            OnPropertyChanged(nameof(HasActiveWork));
            RefreshCommands();
        }
    }
    // A exportação do log não bloqueia a tela: o operador pode exportar o log de uma execução em andamento.
    private bool isExporting;
    public bool IsExporting { get => isExporting; internal set { if (Set(ref isExporting, value)) RefreshCommands(); } }
    private bool isPreview;
    public bool IsPreview { get => isPreview; internal set { if (Set(ref isPreview, value)) RefreshCommands(); } }
    /// <summary>Perfis, visita e configurações só mudam quando nada está em execução ou sendo apagado.</summary>
    public bool IsUnlocked => State is ScreenState.Idle or ScreenState.Loading;
    public bool IsStopVisible => State is ScreenState.Running or ScreenState.Stopping;
    public string StopLabel => State == ScreenState.Stopping ? "Parando…" : "Parar execução";
    public bool HasActiveWork => State is ScreenState.Running or ScreenState.Stopping or ScreenState.Cleaning;
    private string? diagnosticWarning;
    public string? DiagnosticWarning { get => diagnosticWarning; private set { if (Set(ref diagnosticWarning, value)) OnPropertyChanged(nameof(HasDiagnosticWarning)); } }
    public bool HasDiagnosticWarning => !string.IsNullOrWhiteSpace(DiagnosticWarning);

    // Executar visita

    private IReadOnlyList<VisitProfile> profileList = [];
    public IReadOnlyList<VisitProfile> Profiles { get => profileList; private set => Set(ref profileList, value); }
    private VisitProfile? activeProfile;
    public VisitProfile? ActiveProfile
    {
        get => activeProfile;
        set { if (Set(ref activeProfile, value) && !updatingSelection) ActiveProfileChanged(); }
    }
    private IReadOnlyList<PendingVisitChoice> pendingVisits = [];
    public IReadOnlyList<PendingVisitChoice> PendingVisits { get => pendingVisits; private set => Set(ref pendingVisits, value); }
    private PendingVisitChoice? selectedPendingVisit;
    public PendingVisitChoice? SelectedPendingVisit
    {
        get => selectedPendingVisit;
        set { if (Set(ref selectedPendingVisit, value) && !updatingSelection) PendingVisitChanged(); }
    }
    public VisitRun? SelectedRun => SelectedPendingVisit?.Run;
    private bool hasPendingVisits;
    public bool HasPendingVisits { get => hasPendingVisits; private set => Set(ref hasPendingVisits, value); }
    private string pendingNotice = "";
    public string PendingNotice { get => pendingNotice; private set => Set(ref pendingNotice, value); }
    private IReadOnlyList<Floor>? floors;
    public IReadOnlyList<Floor>? Floors { get => floors; private set => Set(ref floors, value); }
    private Floor? selectedFloor;
    public Floor? SelectedFloor { get => selectedFloor; set => Set(ref selectedFloor, value); }
    /// <summary>Uma retomada usa os pavimentos originais da visita; só uma visita nova permite escolhê-los.</summary>
    private bool canChooseFloors = true;
    public bool CanChooseFloors { get => canChooseFloors; private set => Set(ref canChooseFloors, value); }
    private string selectionSummary = "Pavimentos";
    public string SelectionSummary { get => selectionSummary; private set => Set(ref selectionSummary, value); }
    private string executeLabel = "Executar visita preventiva";
    public string ExecuteLabel { get => executeLabel; private set => Set(ref executeLabel, value); }
    private bool canDiscard;
    public bool CanDiscard { get => canDiscard; private set => Set(ref canDiscard, value); }
    private IReadOnlyList<FloorRun>? runFloors;
    public IReadOnlyList<FloorRun>? RunFloors { get => runFloors; private set { if (Set(ref runFloors, value)) OnPropertyChanged(nameof(HasRunFloors)); } }
    public bool HasRunFloors => RunFloors is { Count: > 0 };
    private double progress;
    public double Progress { get => progress; private set => Set(ref progress, value); }
    private string progressCount = "";
    public string ProgressCount { get => progressCount; private set { if (Set(ref progressCount, value)) OnPropertyChanged(nameof(HasStatusRow)); } }
    private string status = "";
    public string Status { get => status; private set => Set(ref status, value); }
    private StatusTone statusTone;
    public StatusTone StatusTone { get => statusTone; private set => Set(ref statusTone, value); }
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public bool HasStatusRow => HasStatus || ProgressCount.Length > 0;

    // Perfis

    private VisitProfile? selectedProfileRow;
    public VisitProfile? SelectedProfileRow { get => selectedProfileRow; set => Set(ref selectedProfileRow, value); }

    // Histórico

    private IReadOnlyList<VisitRun> history = [];
    public IReadOnlyList<VisitRun> History { get => history; private set => Set(ref history, value); }
    private VisitRun? selectedHistoryRun;
    public VisitRun? SelectedHistoryRun
    {
        get => selectedHistoryRun;
        set { if (Set(ref selectedHistoryRun, value) && !updatingSelection) ShowHistoryRun(); }
    }
    private IReadOnlyList<FloorRun>? historyFloors;
    public IReadOnlyList<FloorRun>? HistoryFloors { get => historyFloors; private set => Set(ref historyFloors, value); }
    private FloorRun? selectedHistoryFloor;
    public FloorRun? SelectedHistoryFloor { get => selectedHistoryFloor; set => Set(ref selectedHistoryFloor, value); }
    private string historyMessage = "";
    public string HistoryMessage { get => historyMessage; private set { if (Set(ref historyMessage, value)) OnPropertyChanged(nameof(HasHistoryMessage)); } }
    public bool HasHistoryMessage => !string.IsNullOrWhiteSpace(HistoryMessage);
    private StatusTone historyMessageTone;
    public StatusTone HistoryMessageTone { get => historyMessageTone; private set => Set(ref historyMessageTone, value); }
    private string historyStatus = "";
    public string HistoryStatus { get => historyStatus; private set => Set(ref historyStatus, value); }
    private StatusTone historyStatusTone;
    public StatusTone HistoryStatusTone { get => historyStatusTone; private set => Set(ref historyStatusTone, value); }

    // Configurações

    private string jiraUrl = "";
    public string JiraUrl { get => jiraUrl; set { if (Set(ref jiraUrl, value)) SettingsStatus = ""; } }
    private string portalUrl = "";
    public string PortalUrl { get => portalUrl; set { if (Set(ref portalUrl, value)) SettingsStatus = ""; } }
    private string queueUrl = "";
    public string QueueUrl { get => queueUrl; set { if (Set(ref queueUrl, value)) SettingsStatus = ""; } }
    private string closingTeam = "";
    public string ClosingTeam { get => closingTeam; set { if (Set(ref closingTeam, value)) SettingsStatus = ""; } }
    private string requestType = "";
    public string RequestType { get => requestType; set { if (Set(ref requestType, value)) SettingsStatus = ""; } }
    private string waitingStatus = "";
    public string WaitingStatus { get => waitingStatus; set { if (Set(ref waitingStatus, value)) SettingsStatus = ""; } }
    private string inProgressStatus = "";
    public string InProgressStatus { get => inProgressStatus; set { if (Set(ref inProgressStatus, value)) SettingsStatus = ""; } }
    // Um status por linha.
    private string closedStatuses = "";
    public string ClosedStatuses { get => closedStatuses; set { if (Set(ref closedStatuses, value)) SettingsStatus = ""; } }
    private string startTransition = "";
    public string StartTransition { get => startTransition; set { if (Set(ref startTransition, value)) SettingsStatus = ""; } }
    private string closeTransition = "";
    public string CloseTransition { get => closeTransition; set { if (Set(ref closeTransition, value)) SettingsStatus = ""; } }
    private string issuePath = "";
    public string IssuePath { get => issuePath; set { if (Set(ref issuePath, value)) SettingsStatus = ""; } }
    private string timeoutSeconds = "";
    public string TimeoutSeconds { get => timeoutSeconds; set { if (Set(ref timeoutSeconds, value)) SettingsStatus = ""; } }
    private string loginTimeoutMinutes = "";
    public string LoginTimeoutMinutes { get => loginTimeoutMinutes; set { if (Set(ref loginTimeoutMinutes, value)) SettingsStatus = ""; } }
    public List<FieldLabelItem> FieldLabels { get; }
    private string settingsStatus = "";
    public string SettingsStatus { get => settingsStatus; private set => Set(ref settingsStatus, value); }
    private StatusTone settingsStatusTone;
    public StatusTone SettingsStatusTone { get => settingsStatusTone; private set => Set(ref settingsStatusTone, value); }

    // Aparência: vale na hora e é salva sozinha, sem depender de Salvar configurações nem dos endereços digitados.
    private AppTheme theme = AppTheme.Light;
    public bool IsDarkTheme
    {
        get => theme == AppTheme.Dark;
        set
        {
            var next = value ? AppTheme.Dark : AppTheme.Light;
            if (theme == next) return;
            theme = next; OnPropertyChanged(nameof(IsDarkTheme));
            if (State != ScreenState.Loading && !IsPreview) ThemeSaved = SaveThemeAsync(next);
        }
    }
    /// <summary>A gravação do último tema escolhido; os testes esperam por ela.</summary>
    internal Task ThemeSaved { get; private set; } = Task.CompletedTask;

    private Task SaveThemeAsync(AppTheme value) => SafeAsync(async () =>
    {
        settings.Theme = value;
        await store.SaveSettingsAsync(Serialization.Copy(settings));
    });

    public async Task LoadAsync()
    {
        try
        {
            if (IsPreview) LoadPreview();
            else
            {
                profiles = await store.LoadProfilesAsync(); settings = await store.LoadSettingsAsync(); runs = await store.LoadRunsAsync();
                settings.Diagnostics ??= new DiagnosticOptions();
                diagnostics.Configure(settings.Diagnostics);
                theme = settings.Theme; OnPropertyChanged(nameof(IsDarkTheme));
                diagnostics.Register(profiles);
                if (profiles.Profiles.Count == 0)
                {
                    var first = NewTemplate(); profiles.Profiles.Add(first); profiles.ActiveProfileId = first.Id;
                    await store.SaveProfilesAsync(profiles);
                }
            }
            RefreshProfiles(); RefreshHistory();
            ShowSettings();
            State = ScreenState.Idle;
            await diagnostics.CleanAsync();
        }
        catch (Exception ex)
        {
            diagnostics.AppEvent("app.initialize_failed", "Não foi possível carregar os dados locais.", DiagnosticLevel.Error, ex, code: "INITIALIZATION_FAILED");
            // Sem os dados locais, nenhuma visita pode ser executada; a tela continua bloqueada como no carregamento.
            State = ScreenState.Loading;
            dialogs.ShowError(ex.Message);
        }
    }

    /// <summary>Mostra perfis e visitas já carregados, sem ler o disco. Usado pelos testes e pelas capturas.</summary>
    internal void Show(ProfileDocument document, List<VisitRun> visits, AppSettings? configured = null)
    {
        profiles = document; runs = visits; settings = configured ?? settings;
        RefreshProfiles(); RefreshHistory();
        ShowSettings();
        State = ScreenState.Idle;
    }

    private void LoadPreview()
    {
        var example = NewTemplate();
        example.Name = "Unidade de exemplo"; example.ReporterName = "Administrador da unidade";
        example.Unit = "Configure o nome da sua unidade";
        profiles.Profiles.Add(example); profiles.ActiveProfileId = example.Id;
        if (!pendingPreview) return;
        var stages = new[] { FloorStage.Closed, FloorStage.Closed, FloorStage.Started, FloorStage.Assigned, FloorStage.Created, FloorStage.Pending };
        runs.Add(new VisitRun
        {
            Profile = Serialization.Copy(example), State = RunState.Interrupted,
            CreatedAt = DateTimeOffset.Now.AddMinutes(-8), Message = "Execução interrompida. Retome para continuar.",
            Floors = example.Floors.Select((f, i) => new FloorRun
            { Floor = Serialization.Copy(f), IssueKey = stages[i % stages.Length] == FloorStage.Pending ? null : $"SD-{1001 + i}", Stage = stages[i % stages.Length], Message = PreviewMessage(stages[i % stages.Length]) }).ToList()
        });
        // Outros estados no histórico, de perfis distintos para não gerar pendência no perfil ativo.
        foreach (var (runState, label, minutes) in new[] { (RunState.Completed, "Concluída", -1500), (RunState.NeedsReconciliation, "Vincular", -2900), (RunState.Failed, "Falha", -4400) })
        {
            var other = Serialization.Copy(example); other.Id = Guid.NewGuid().ToString("N"); other.Name = $"Unidade {label}";
            runs.Add(new VisitRun { Profile = other, State = runState, CreatedAt = DateTimeOffset.Now.AddMinutes(minutes), Message = label,
                Floors = other.Floors.Select(f => new FloorRun { Floor = f, Stage = runState == RunState.Completed ? FloorStage.Closed : FloorStage.Pending }).ToList() });
        }
    }

    // Mesmas mensagens que o motor grava em cada etapa, para que as capturas do README mostrem o fluxo real.
    private static string PreviewMessage(FloorStage stage) => stage switch
    {
        FloorStage.Closed => "Fechado e conferido",
        FloorStage.Started => "Em atendimento",
        FloorStage.Assigned => "Atribuição conferida",
        FloorStage.Created => "Chamado aberto",
        _ => "Aguardando"
    };

    internal static VisitProfile NewTemplate() => new()
    {
        Floors = new[] { "Subsolo", "Térreo", "1° Pavimento", "2° Pavimento", "3° Pavimento", "4° Pavimento" }
            .Select(name => new Floor
            {
                Name = name,
                Resolution = $"Realizada visita preventiva {name}\n\nEquipamentos e serviços verificados.\nObservações: preencha o resultado da visita."
            }).ToList()
    };

    private void RefreshProfiles(string? selectedId = null)
    {
        var id = selectedId ?? profiles.ActiveProfileId;
        updatingSelection = true;
        try
        {
            Profiles = profiles.Profiles.ToList();
            ActiveProfile = Profiles.FirstOrDefault(p => p.Id == id) ?? Profiles.FirstOrDefault();
            if (SelectedProfileRow is not null && !Profiles.Contains(SelectedProfileRow)) SelectedProfileRow = null;
        }
        finally { updatingSelection = false; }
        RefreshPendingVisits(preferProfile: true);
    }

    internal void RefreshPendingVisits(string? selectedId = null, bool preferProfile = false)
    {
        var id = selectedId ?? (preferProfile ? null : SelectedRun?.Id);
        var choices = VisitRunSelection.Pending(runs)
            .Select(run => new PendingVisitChoice(run, profiles.Profiles.All(p => p.Id != run.Profile.Id))).ToList();
        var selected = choices.FirstOrDefault(choice => choice.Run.Id == id);
        if (selected is null && ActiveProfile is not null)
        {
            try
            {
                var preferred = VisitRunSelection.PendingForProfile(runs, ActiveProfile.Id);
                selected = choices.FirstOrDefault(choice => choice.Run == preferred);
            }
            catch (InvalidOperationException) { /* A escolha explícita fica na tela principal. */ }
        }
        updatingSelection = true;
        try { PendingVisits = choices; SelectedPendingVisit = selected; }
        finally { updatingSelection = false; }
        HasPendingVisits = choices.Count > 0;
        ShowExecutionProfile();
    }

    private void PendingVisitChanged()
    {
        if (SelectedRun is { } run)
        {
            updatingSelection = true;
            try { ActiveProfile = profiles.Profiles.FirstOrDefault(p => p.Id == run.Profile.Id); }
            finally { updatingSelection = false; }
        }
        ShowExecutionProfile();
        if (SelectedRun is { } selected) ShowProgress(selected);
    }

    private void ActiveProfileChanged()
    {
        if (ActiveProfile is null) return;
        profiles.ActiveProfileId = ActiveProfile.Id;
        RefreshPendingVisits(preferProfile: true);
        if (SelectedRun is { } run) ShowProgress(run);
        else { Progress = 0; ProgressCount = ""; }
        if (State != ScreenState.Loading && !IsPreview) _ = SafeAsync(() => store.SaveProfilesAsync(profiles));
    }

    private bool ProfileHasPending => ActiveProfile is not null && runs.Any(r => r.Profile.Id == ActiveProfile.Id && r.State != RunState.Completed);

    private void ShowExecutionProfile()
    {
        var run = SelectedRun;
        var profile = run?.Profile ?? ActiveProfile;
        Floors = run?.Floors.Select(f => f.Floor).ToList() ?? profile?.Floors.ToList();
        SelectedFloor = Floors?.FirstOrDefault();
        ShowRunFloors(run);
        ShowRunStatus(run);
        CanChooseFloors = run is null;
        ExecuteLabel = run is not null || ProfileHasPending ? "Retomar visita preventiva" : "Executar visita preventiva";
        CanDiscard = run is not null && VisitRunSelection.CanDiscard(run);
        if (run is not null)
        {
            var count = run.Floors.Count(f => f.IssueKey is not null);
            PendingNotice = $"Visita pendente de {run.DateLabel}: {count} chamados registrados. A retomada usará os dados originais e o progresso desta visita.";
        }
        else PendingNotice = ProfileHasPending
            ? "Selecione a visita pendente que deseja retomar. Nenhum chamado será aberto antes dessa escolha."
            : "Para retomar, escolha uma visita pendente acima. Para uma nova visita, use o perfil selecionado.";
        UpdateSelectionSummary();
        RefreshCommands();
    }

    private void ShowRunFloors(VisitRun? run)
    {
        RunFloors = run?.Floors.ToList();
        Progress = run is null || run.Floors.Count == 0 ? 0 : run.Floors.Sum(f => (int)f.Stage) * 100d / (run.Floors.Count * 4);
        ProgressCount = run is { Floors.Count: > 0 } ? run.ProgressLabel : "";
    }

    // A mensagem salva pelo motor explica o passo atual, o pedido de login e o motivo de uma falha.
    private void ShowRunStatus(VisitRun? run) => ShowStatus(run?.Message, run is null ? StatusTone.Text : StatusTones.For(run.State));

    private void ShowStatus(string? text, StatusTone tone = StatusTone.Text)
    {
        Status = text ?? "";
        StatusTone = tone;
        OnPropertyChanged(nameof(HasStatus)); OnPropertyChanged(nameof(HasStatusRow));
    }

    public void UpdateSelectionSummary() => SelectionSummary = SelectedRun is { } run
        ? $"Pavimentos da visita: {run.Floors.Count}"
        : $"Pavimentos selecionados: {ActiveProfile?.Floors.Count(f => f.Selected) ?? 0}";

    private bool CanExecuteVisit() => State == ScreenState.Idle && (SelectedRun is not null || (ActiveProfile is not null && !ProfileHasPending));

    internal void ShowProgress(VisitRun run)
    {
        ShowRunFloors(run);
        ShowRunStatus(run);
        RefreshHistory();
    }

    // Execução

    public async Task ExecuteAsync()
    {
        if (State != ScreenState.Idle) return;
        await SafeAsync(async () =>
        {
            if (IsPreview) throw new InvalidOperationException("A prévia é somente para conferir a tela.");
            await RunBusyAsync(ExecuteVisitAsync);
        });
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> work)
    {
        State = ScreenState.Running; cancellation = new CancellationTokenSource();
        try { activeTask = work(cancellation.Token); await activeTask; }
        finally { activeTask = null; cancellation.Dispose(); cancellation = null; State = ScreenState.Idle; }
    }

    private async Task ExecuteVisitAsync(CancellationToken token)
    {
        var pending = SelectedRun is { } selected ? VisitRunSelection.ForResume(runs, selected)
            : ActiveProfile is not null ? VisitRunSelection.PendingForProfile(runs, ActiveProfile.Id) : null;
        if (pending is not null)
        {
            RefreshHistory(pending.Id);
            RefreshPendingVisits(pending.Id);
            VisitRunSelection.ApplySettings(pending, settings);
            await RunAsync(pending, token, resume: true);
            return;
        }
        if (ActiveProfile is null) throw new InvalidOperationException("Escolha um perfil ou uma visita pendente para continuar.");
        var errors = ProfileValidation.Validate(ActiveProfile, settings);
        // Os campos a corrigir ficam no painel de status, um por linha, em vez de numa janela.
        if (errors.Count > 0) { ShowStatus(string.Join(Environment.NewLine, errors), StatusTone.Danger); return; }
        await store.SaveProfilesAsync(profiles);
        var run = ProfileValidation.CreateRun(ActiveProfile, settings);
        await store.SaveRunAsync(run);
        runs.Insert(0, run); RefreshHistory();
        token.ThrowIfCancellationRequested();
        await RunAsync(run, token);
    }

    private async Task RunAsync(VisitRun run, CancellationToken token, bool resume = false)
    {
        try
        {
            if (resume)
            {
                var resumed = await engine.ResumeAsync(run, floor => OnUiAsync(() => token.IsCancellationRequested ? null : dialogs.AskIssueKey(floor.Name)), token,
                    (floor, snapshot) => OnUiAsync(() => !token.IsCancellationRequested && dialogs.ConfirmCloseRetry(floor, snapshot)));
                if (resumed) ShowProgress(run);
            }
            else { await engine.ExecuteAsync(run, token); ShowProgress(run); }
        }
        finally
        {
            RefreshHistory(run.Id);
            RefreshPendingVisits(run.State == RunState.Completed ? null : run.Id);
            if (run.State == RunState.Completed) ShowRunFloors(run);
            ShowRunStatus(run);
        }
    }

    public void Stop()
    {
        if (State != ScreenState.Running) return;
        cancellation?.Cancel(); State = ScreenState.Stopping;
        ShowStatus("Parando com segurança… a etapa em andamento será concluída antes de interromper.", StatusTone.Muted);
    }

    public async Task CheckAccessAsync()
    {
        if (State != ScreenState.Idle || IsPreview) return;
        await SafeAsync(() => RunBusyAsync(CheckAccessCoreAsync));
    }

    private async Task CheckAccessCoreAsync(CancellationToken token)
    {
        diagnostics.AppEvent("access.started", "Verificação de acesso iniciada.");
        ShowStatus("Abrindo o Edge para verificar o acesso ao Jira…");
        try
        {
            await using var browser = automationFactory(store);
            await browser.ConnectAsync(settings, message =>
            {
                diagnostics.AppEvent("access.progress", message);
                OnUi(() => ShowStatus(message));
            }, token);
            diagnostics.AppEvent("access.confirmed", "Acesso ao formulário confirmado.");
            ShowStatus("Acesso confirmado: o formulário de visita preventiva carregou no Edge.", StatusTone.Success);
        }
        // O resultado fica no painel de status; uma janela de erro repetiria a mesma informação.
        catch (OperationCanceledException)
        {
            diagnostics.AppEvent("access.cancelled", "Verificação de acesso interrompida.");
            ShowStatus("Verificação de acesso interrompida.", StatusTone.Attention);
        }
        catch (Exception ex)
        {
            diagnostics.AppEvent("access.failed", "Não foi possível confirmar o acesso.", DiagnosticLevel.Error, ex, code: "ACCESS_CHECK_FAILED");
            ShowStatus("Não foi possível confirmar o acesso. " + ex.Message, StatusTone.Danger);
        }
    }

    // Só visitas sem chamado no Jira podem ser descartadas; as demais exigem retomada ou conferência.
    public async Task DiscardAsync()
    {
        if (State != ScreenState.Idle || IsExporting || IsPreview || SelectedRun is not { } run || !VisitRunSelection.CanDiscard(run)) return;
        var message = $"Descartar a visita de {run.DateLabel} do perfil “{run.ProfileName}”?\n\nNenhum chamado foi aberto no Jira para esta visita. O registro, os logs e as capturas dela serão apagados deste computador, e o perfil poderá iniciar uma nova visita.\n\nEsta ação não pode ser desfeita.";
        if (!dialogs.ConfirmDanger("Descartar visita", message, "Descartar visita")) return;
        State = ScreenState.Cleaning;
        ShowStatus("Descartando a visita…", StatusTone.Muted);
        Exception? failure = null;
        try { activeTask = store.DeleteRunAsync(run.Id); await activeTask; }
        catch (Exception ex)
        {
            failure = ex;
            diagnostics.AppEvent("run.discard_failed", "A visita pendente não pôde ser descartada.", DiagnosticLevel.Error, ex, run.Id);
        }
        finally
        {
            if (!File.Exists(Path.Combine(store.Root, "runs", run.Id + ".json"))) runs.Remove(run);
            activeTask = null; State = ScreenState.Idle;
            RefreshHistory(); RefreshPendingVisits(preferProfile: true);
        }
        if (failure is null)
        {
            diagnostics.AppEvent("run.discarded", "Visita pendente sem chamados descartada.");
            ShowStatus("Visita descartada. O perfil já pode iniciar uma nova visita.", StatusTone.Success);
        }
        else ShowStatus("Não foi possível descartar a visita. " + failure.Message, StatusTone.Danger);
    }

    // Perfis

    public Task NewProfileAsync() => SafeAsync(() => EditAsync(NewTemplate(), true));
    public Task EditProfileAsync() => SafeAsync(() => EditAsync(SelectedProfileRow));

    private async Task EditAsync(VisitProfile? profile, bool isNew = false)
    {
        if (!IsUnlocked || profile is null) return;
        if (dialogs.EditProfile(profile) is { } edited) await SaveProfileAsync(edited, isNew);
    }

    private async Task SaveProfileAsync(VisitProfile profile, bool newProfile)
    {
        if (profiles.Profiles.Any(p => p.Id != profile.Id && string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Já existe um perfil com este nome. Escolha outro nome.");
        if (newProfile) profiles.Profiles.Add(profile);
        else profiles.Profiles[profiles.Profiles.FindIndex(p => p.Id == profile.Id)] = profile;
        profiles.ActiveProfileId = profile.Id;
        await store.SaveProfilesAsync(profiles);
        RefreshProfiles(profile.Id);
    }

    public async Task DeleteProfileAsync()
    {
        if (SelectedProfileRow is not { } profile || !IsUnlocked) return;
        if (!dialogs.Confirm($"Excluir o perfil “{profile.Name}”? O histórico das visitas será mantido.")) return;
        await SafeAsync(async () =>
        {
            profiles.Profiles.Remove(profile); profiles.ActiveProfileId = profiles.Profiles.FirstOrDefault()?.Id;
            await store.SaveProfilesAsync(profiles); RefreshProfiles();
        });
    }

    // Histórico

    private void RefreshHistory(string? selectedId = null)
    {
        var id = selectedId ?? SelectedHistoryRun?.Id;
        updatingSelection = true;
        try
        {
            History = runs.ToList();
            SelectedHistoryRun = runs.FirstOrDefault(r => r.Id == id)
                ?? runs.FirstOrDefault(r => r.Profile.Id == ActiveProfile?.Id && r.State != RunState.Completed && r.Floors.Any(f => f.IssueKey is not null))
                ?? runs.FirstOrDefault();
            // A lista nova pode ter apagado a seleção na tela; avisar de novo restaura a visita escolhida.
            OnPropertyChanged(nameof(SelectedHistoryRun));
        }
        finally { updatingSelection = false; }
        ShowHistoryRun();
    }

    // O motivo de uma falha ou interrupção fica visível sem precisar abrir o log.
    private void ShowHistoryRun()
    {
        var run = SelectedHistoryRun;
        var selectedFloorId = SelectedHistoryFloor?.Floor.Id;
        HistoryFloors = run?.Floors.ToList();
        SelectedHistoryFloor = run?.Floors.FirstOrDefault(f => f.Floor.Id == selectedFloorId) ?? run?.Floors.FirstOrDefault();
        HistoryMessage = run?.Message ?? "";
        HistoryMessageTone = run is null ? StatusTone.Text : StatusTones.For(run.State);
    }

    public async Task ExportLogAsync()
    {
        if (State == ScreenState.Cleaning || IsExporting) return;
        IsExporting = true;
        try { await SafeAsync(() => dialogs.ExportLogAsync(SelectedHistoryRun)); }
        finally { IsExporting = false; }
    }

    public async Task ClearHistoryAsync()
    {
        if (State != ScreenState.Idle || IsExporting || IsPreview) return;
        var pending = VisitRunSelection.Pending(runs).Count;
        var message = $"Apagar todo o histórico deste computador?\n\n{runs.Count} visita(s), incluindo {pending} pendente(s), serão removidas de todas as unidades. Os logs e capturas das visitas também serão apagados.\n\nAs visitas pendentes não poderão mais ser retomadas pelo Vistora. Os chamados no Jira, perfis, configurações e login do Edge serão preservados.\n\nEsta ação não pode ser desfeita.";
        if (!dialogs.ConfirmDanger("Limpar histórico e pendências", message, "Limpar tudo")) return;
        State = ScreenState.Cleaning;
        HistoryStatus = "Limpando histórico e pendências…"; HistoryStatusTone = StatusTone.Muted;
        Exception? failure = null;
        try { activeTask = store.ClearHistoryAsync(); await activeTask; }
        catch (Exception ex)
        {
            failure = ex;
            diagnostics.AppEvent("history.clear_failed", "A limpeza do histórico não pôde ser concluída.", DiagnosticLevel.Error, ex);
        }
        finally
        {
            try { runs = await store.LoadRunsAsync(); }
            catch (Exception ex)
            {
                failure ??= ex;
                runs.RemoveAll(run => !File.Exists(Path.Combine(store.Root, "runs", run.Id + ".json")));
            }
            RefreshProfiles(profiles.ActiveProfileId); RefreshHistory();
            activeTask = null; State = ScreenState.Idle;
        }
        HistoryStatusTone = failure is null ? StatusTone.Success : StatusTone.Danger;
        HistoryStatus = failure is null
            ? "Histórico, pendências, logs e capturas das visitas removidos."
            : "A limpeza ficou incompleta. Os registros restantes foram mantidos. Tente novamente. " + failure.Message;
        if (failure is null) diagnostics.AppEvent("history.cleared", "Histórico e diagnósticos das visitas removidos.");
    }

    // Configurações

    public async Task SaveSettingsAsync()
    {
        if (!IsUnlocked) return;
        var copy = Serialization.Copy(settings);
        copy.JiraUrl = JiraUrl.Trim().TrimEnd('/'); copy.PortalUrl = PortalUrl.Trim(); copy.QueueUrl = QueueUrl.Trim();
        copy.ClosingTeam = ClosingTeam.Trim();
        copy.Jira.RequestType = RequestType.Trim(); copy.Jira.WaitingStatus = WaitingStatus.Trim(); copy.Jira.InProgressStatus = InProgressStatus.Trim();
        copy.Jira.ClosedStatuses = ClosedStatuses.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        copy.Jira.StartTransition = StartTransition.Trim(); copy.Jira.CloseTransition = CloseTransition.Trim(); copy.Jira.IssuePath = IssuePath.Trim();
        // Um número inválido fica fora do intervalo e a validação explica o limite.
        copy.TimeoutSeconds = int.TryParse(TimeoutSeconds.Trim(), out var timeout) ? timeout : 0;
        copy.LoginTimeoutMinutes = int.TryParse(LoginTimeoutMinutes.Trim(), out var login) ? login : 0;
        // Só rótulos diferentes do padrão ficam no arquivo.
        copy.FieldLabels = FieldLabels.Where(f => !string.IsNullOrWhiteSpace(f.Label) &&
                !string.Equals(f.Label.Trim(), f.Field.Default, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f.Field.Key, f => f.Label.Trim());
        copy.Diagnostics ??= new DiagnosticOptions();
        var errors = ProfileValidation.ValidateSettings(copy);
        if (errors.Count > 0) { ShowSettingsStatus(string.Join(Environment.NewLine, errors), true); return; }
        await SafeAsync(async () =>
        {
            await store.SaveSettingsAsync(copy); settings = copy;
            diagnostics.Configure(copy.Diagnostics);
            ShowSettingsStatus("Configurações salvas.", false);
        });
    }

    private void ShowSettings()
    {
        JiraUrl = settings.JiraUrl; PortalUrl = settings.PortalUrl; QueueUrl = settings.QueueUrl;
        ClosingTeam = settings.ClosingTeam;
        var workflow = settings.Jira;
        RequestType = workflow.RequestType; WaitingStatus = workflow.WaitingStatus; InProgressStatus = workflow.InProgressStatus;
        ClosedStatuses = string.Join(Environment.NewLine, workflow.ClosedStatuses);
        StartTransition = workflow.StartTransition; CloseTransition = workflow.CloseTransition; IssuePath = workflow.IssuePath;
        TimeoutSeconds = settings.TimeoutSeconds.ToString(); LoginTimeoutMinutes = settings.LoginTimeoutMinutes.ToString();
        foreach (var item in FieldLabels) item.Label = JiraFields.Label(settings, item.Field.Key);
        SettingsStatus = "";
    }

    private void ShowSettingsStatus(string text, bool error)
    {
        SettingsStatus = text;
        SettingsStatusTone = error ? StatusTone.Danger : StatusTone.Success;
    }

    // Encerramento: a etapa em andamento termina e os registros são gravados antes de fechar.
    public async Task ShutdownAsync()
    {
        cancellation?.Cancel();
        try { if (activeTask is not null) await activeTask; }
        catch (Exception ex) { diagnostics.AppEvent("app.shutdown_failed", "Falha ao aguardar a execução no encerramento.", DiagnosticLevel.Error, ex); }
        await diagnostics.FlushAsync();
    }

    private async Task SafeAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { diagnostics.AppEvent("ui.operation_cancelled", "Operação interrompida."); }
        // InvalidOperationException traz uma regra que o operador pode corrigir; as demais são falhas.
        catch (InvalidOperationException exception)
        {
            diagnostics.AppEvent("ui.operation_refused", "Uma operação da interface foi recusada.", DiagnosticLevel.Warning, exception);
            dialogs.ShowWarning(exception.Message);
        }
        catch (Exception exception)
        {
            diagnostics.AppEvent("ui.operation_failed", "Uma operação da interface falhou.", DiagnosticLevel.Error, exception);
            dialogs.ShowError(exception.Message);
        }
    }

    // O motor e a verificação de acesso avisam de outras threads; a tela só muda na thread em que o ViewModel foi criado.
    private void OnUi(Action action)
    {
        if (ui is null || SynchronizationContext.Current == ui) action();
        else ui.Send(_ => action(), null);
    }
    private void PostToUi(Action action)
    {
        if (ui is null) action();
        else ui.Post(_ => action(), null);
    }
    private Task<T> OnUiAsync<T>(Func<T> function)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        PostToUi(() =>
        {
            try { result.SetResult(function()); }
            catch (Exception ex) { result.SetException(ex); }
        });
        return result.Task;
    }

    private void RefreshCommands() { foreach (var command in commands ?? []) command.RaiseCanExecuteChanged(); }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
