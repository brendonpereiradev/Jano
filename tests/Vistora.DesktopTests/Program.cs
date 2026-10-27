using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vistora.Core;
using Vistora.Desktop;
using Vistora.Infrastructure;

// As regras da tela principal são conferidas no MainViewModel; a janela só tem os testes de abertura e das capturas.
internal static class Program
{
    private static string? screenshotDirectory;

    [STAThread]
    private static int Main(string[] args)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        if (args.Length == 2 && args[0] == "--screenshots") screenshotDirectory = Path.GetFullPath(args[1]);
        var tests = new (string Name, Action Test)[]
        {
            ("Janela abre dentro da tela com controles de mover e redimensionar", StartupWindowFitsScreen),
            ("Abertura respeita a escala e permite aumentar o tamanho depois", StartupSizingAcrossScales),
            ("Janela mostra o estado do ViewModel e gera as capturas", WindowShowsViewModel),
            ("Várias visitas exigem escolha na tela principal", AmbiguousVisits),
            ("Perfil excluído continua permitindo retomada", DeletedProfile),
            ("Retomada preserva os dados originais e prioriza progresso", OriginalData),
            ("Trocar perfil e bloquear a tela preservam a seleção", ProfileAndBusy),
            ("Conclusão remove a pendência e permite nova visita", CompletedVisit),
            ("Selecionar o histórico não altera a retomada", HistoryIsReadOnly),
            ("Cancelar a limpeza preserva dados e a confirmação informa seu alcance", CancelHistoryCleanup),
            ("Limpeza esvazia histórico e pendências e restaura o perfil", ClearHistory),
            ("Limpeza respeita carregamento, execução, exportação e prévia", CleanupGuards),
            ("Falha parcial de limpeza mantém registros restantes na tela", PartialHistoryCleanup),
            ("Painel de status mostra a mensagem e a cor do estado da visita", RunStatusPanel),
            ("Visita sem chamados pode ser descartada e libera nova visita", DiscardVisit),
            ("Histórico mostra o motivo da visita selecionada com a cor do estado", HistoryMessage),
            ("Perfil incompleto mostra os erros no painel, sem abrir janela", IncompleteProfile),
            ("Verificar acesso informa sucesso, falha e interrupção no painel", AccessCheck),
            ("Configurações recusam endereço inválido e salvam o corrigido", SettingsValidation),
            ("Tema escuro vale na hora, inclusive nas cores de estado, e fica salvo", ThemeSwitch),
            ("Confirmação de exclusão usa Cancelar como padrão", DangerConfirmDefaults),
            ("Barra de título traz a marca, o tema e o emblema ativo durante o trabalho", TitleBar),
            ("Mensagens próprias mantêm os botões e o padrão da MessageBox", MessageDefaults),
            ("Editor de perfil marca o campo inválido na própria tela", ProfileEditorValidation),
            ("Cancelar o editor com alterações pede confirmação", ProfileEditorUnsavedChanges)
        };
        var failed = 0;
        foreach (var (name, test) in tests)
            try { test(); Console.WriteLine($"PASS: {name}"); }
            catch (Exception ex) { failed++; Console.WriteLine($"FAIL: {name}: {ex}"); }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} verificações da interface aprovadas.");
        app.Shutdown();
        return failed == 0 ? 0 : 1;
    }

    private static VisitProfile Profile(string name = "Unidade A") => new()
    {
        Name = name, Unit = name, ReporterName = "Solicitante de exemplo", ReporterEmail = "pessoa@example.com",
        FullName = "Pessoa Exemplo", Extension = "123", Floors = [new Floor { Name = "Pavimento 1", Resolution = "Equipamentos conferidos." }]
    };
    private static VisitRun Run(VisitProfile profile, string key = "SD-1001")
    {
        var run = ProfileValidation.CreateRun(profile, new AppSettings());
        run.Floors[0].IssueKey = key; run.Floors[0].Stage = FloorStage.Created; run.Message = "Chamado aberto";
        return run;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static T Control<T>(Window window, string name) where T : class => (T)window.FindName(name);
    private static void Select(MainViewModel model, VisitRun run) => model.SelectedPendingVisit = model.PendingVisits.Single(choice => choice.Run == run);
    private static string RunFile(MainViewModel model, VisitRun run) => Path.Combine(model.Store.Root, "runs", run.Id + ".json");

    private static MainWindow Open(MainViewModel model)
    {
        var window = new MainWindow(model) { ShowActivated = false, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout();
        return window;
    }

    private static void StartupWindowFitsScreen()
    {
        using var fixture = new Fixture([Profile()], []);
        var window = new MainWindow(fixture.Model) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var handle = new WindowInteropHelper(window).Handle;
            var monitor = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
            Check(GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor) && GetWindowRect(handle, out var bounds), "Não foi possível conferir a área útil do monitor.");
            Check(GetWindowRect(handle, out bounds) && bounds.Left >= monitor.Work.Left && bounds.Top >= monitor.Work.Top &&
                bounds.Right <= monitor.Work.Right && bounds.Bottom <= monitor.Work.Bottom,
                $"A janela ultrapassa a área útil: ({bounds.Left}, {bounds.Top}, {bounds.Right}, {bounds.Bottom}).");
            var style = GetWindowLong(handle, -16);
            const int controls = 0x00C00000 | 0x00080000 | 0x00040000 | 0x00020000 | 0x00010000;
            Check((style & controls) == controls && window.Icon is not null, "Barra de título, ícone ou controles de janela ausentes.");
            var original = bounds;
            window.Left += 8; window.Top += 8;
            // Em telas menores que o tamanho mínimo da janela (como a do executor da CI), a abertura já usa o mínimo.
            var shrink = Math.Min(12, window.Width - window.MinWidth);
            if (shrink <= 0) Console.WriteLine($"Aviso: a janela abriu no tamanho mínimo ({window.Width:0}x{window.Height:0}); redução não conferida nesta tela.");
            window.Width -= Math.Max(0, shrink);
            window.UpdateLayout();
            Check(GetWindowRect(handle, out bounds) && bounds.Left > original.Left && bounds.Top > original.Top,
                "A janela não permite movimentação.");
            Check(shrink <= 0 || bounds.Right - bounds.Left < original.Right - original.Left, "A janela não permite redução de tamanho.");
            window.Width += 24; window.UpdateLayout();
            Check(GetWindowRect(handle, out bounds) && bounds.Right - bounds.Left > original.Right - original.Left,
                "A janela não permite aumentar o tamanho.");
            var editor = new ProfileWindow(Profile()) { Owner = window, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                editor.Show(); editor.UpdateLayout();
                var editorHandle = new WindowInteropHelper(editor).Handle;
                Check(GetMonitorInfo(MonitorFromWindow(editorHandle, 2), ref monitor) && GetWindowRect(editorHandle, out var editorBounds) &&
                    editorBounds.Left >= monitor.Work.Left && editorBounds.Top >= monitor.Work.Top &&
                    editorBounds.Right <= monitor.Work.Right && editorBounds.Bottom <= monitor.Work.Bottom && editor.Icon is not null,
                    "A janela de perfil abre com a barra de título fora da tela.");
            }
            finally { editor.Close(); }
        }
        finally { window.Close(); }
    }
    private static void StartupSizingAcrossScales()
    {
        foreach (var scale in new[] { 1d, 1.25d, 1.5d, 2d })
        {
            using var fixture = new Fixture([Profile()], []);
            var window = new MainWindow(fixture.Model);
            var work = new Rect(-1920 / scale, -80 / scale, 1920 / scale, 1040 / scale);
            WindowPlacement.FitToWorkArea(window, work);
            Check(work.Contains(new Rect(window.Left, window.Top, window.Width, window.Height)),
                $"Abertura fora da área útil em {scale * 100}%.");
            Check(window.MinWidth <= window.Width && window.MinHeight <= window.Height,
                $"Tamanho mínimo impede a janela de caber em {scale * 100}%.");
            var width = window.Width; var height = window.Height;
            window.Width += 24; window.Height += 24;
            Check(window.Width == width + 24 && window.Height == height + 24 &&
                double.IsPositiveInfinity(window.MaxWidth) && double.IsPositiveInfinity(window.MaxHeight),
                "O ajuste inicial criou uma restrição permanente de tamanho.");
            window.Close();
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref NativeMonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);

    // Um percurso pelas quatro páginas: confere que os controles acompanham o ViewModel e salva as capturas pedidas.
    private static void WindowShowsViewModel()
    {
        var first = Profile(); var second = Profile("Unidade B"); var run = Run(first);
        run.State = RunState.Failed; run.Message = "O campo “Andar” não corresponde a Pavimento 1.";
        var completed = Run(second); completed.State = RunState.Completed; completed.Floors[0].Stage = FloorStage.Closed; completed.Message = "Visita concluída.";
        using var fixture = new Fixture([first, second], [run, completed]); var model = fixture.Model;
        var window = Open(model);
        Brush Color(string key) => (Brush)Application.Current.Resources[key];
        try
        {
            var status = Control<TextBlock>(window, "RunStatus"); var execute = Control<Button>(window, "ExecuteButton");
            Check(status.Visibility == Visibility.Visible && status.Text == run.Message && status.Foreground == Color("AppDangerTextBrush"),
                "O painel de status não mostrou o motivo da falha em vermelho.");
            Check(Equals(Control<ComboBox>(window, "PendingVisitPicker").SelectedItem, model.SelectedPendingVisit) && model.SelectedPendingVisit is not null &&
                Control<DataGridColumn>(window, "VisitColumn").Visibility == Visibility.Collapsed &&
                execute.Content.Equals("Retomar visita preventiva") && execute.IsEnabled && Control<TextBlock>(window, "RunCount").Text == "0 de 1 fechados",
                "A tela não acompanhou a visita pendente escolhida.");
            Screenshot(window, "status-falha");
            Control<ComboBox>(window, "ProfilePicker").SelectedItem = second;
            Check(model.ActiveProfile == second && model.SelectedRun is null && Control<ComboBox>(window, "PendingVisitPicker").SelectedItem is null &&
                Control<DataGridColumn>(window, "VisitColumn").Visibility == Visibility.Visible && Control<Grid>(window, "RunStatusRow").Visibility == Visibility.Collapsed &&
                execute.Content.Equals("Executar visita preventiva"), "Trocar o perfil na tela não atualizou o ViewModel.");
            Screenshot(window, "executar");
            Control<ComboBox>(window, "PendingVisitPicker").SelectedItem = model.PendingVisits.Single();
            Check(model.SelectedRun == run && model.ActiveProfile == first, "Escolher a visita pendente na tela não chegou ao ViewModel.");
            model.State = ScreenState.Running;
            var stop = Control<Button>(window, "StopButton");
            Check(stop.Visibility == Visibility.Visible && stop.IsEnabled && stop.Style == Application.Current.Resources["Danger"] &&
                !execute.IsEnabled && !Control<ComboBox>(window, "PendingVisitPicker").IsEnabled, "A execução não bloqueou a tela nem destacou Parar.");
            Screenshot(window, "executando");
            model.Stop();
            Check(stop.Content.Equals("Parando…") && !stop.IsEnabled && status.Foreground == Color("AppTextSecondaryBrush"), "Parar não apareceu na tela.");
            Screenshot(window, "parando");
            model.State = ScreenState.Idle;
            Check(stop.Visibility == Visibility.Collapsed && execute.IsEnabled, "A tela não foi liberada depois da execução.");

            window.SelectPage(1);
            Check(Control<DataGrid>(window, "ProfilesGrid").Items.Count == 2, "A lista de perfis não acompanhou o ViewModel.");
            Screenshot(window, "perfis");
            window.SelectPage(2);
            var history = Control<DataGrid>(window, "HistoryGrid"); var message = Control<TextBlock>(window, "HistoryRunMessage");
            Check(history.SelectedItem == run && message.Text == run.Message && message.Foreground == Color("AppDangerTextBrush"), "O Histórico não mostrou a visita escolhida.");
            Screenshot(window, "historico");
            history.SelectedItem = completed;
            Check(model.SelectedHistoryRun == completed && message.Foreground == Color("AppSuccessTextBrush") &&
                Control<DataGrid>(window, "RunFloorGrid").Items.Cast<FloorRun>().Single() == completed.Floors[0], "Escolher no Histórico não chegou ao ViewModel.");
            window.SelectPage(3);
            var jira = Control<TextBox>(window, "JiraUrlBox");
            Check(jira.Text == model.JiraUrl, "As configurações não apareceram na tela.");
            jira.Text = "https://outro.example.com";
            Check(model.JiraUrl == "https://outro.example.com", "Editar o endereço não chegou ao ViewModel.");
            Screenshot(window, "configuracoes");
            window.SelectPage(0);
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            Screenshot(window, "executar-tamanho-minimo");
        }
        finally { window.Close(); }
        var editor = new ProfileWindow(first);
        Screenshot(editor, "editar-perfil"); editor.Close();
    }

    private static void AmbiguousVisits()
    {
        var profile = Profile(); var first = Run(profile); var second = Run(profile, "SD-1002");
        using var fixture = new Fixture([profile], [first, second]); var model = fixture.Model;
        Check(model.SelectedRun is null && !model.ExecuteCommand.CanExecute(null), "Escolheu uma visita ambígua automaticamente.");
        Select(model, second);
        Check(model.SelectedRun == second && model.ExecuteCommand.CanExecute(null), "Seleção explícita não habilitou a retomada.");
        Check(VisitRunSelection.ForResume([first, second], model.SelectedRun!) == second, "A seleção foi substituída.");
    }

    private static void DeletedProfile()
    {
        var run = Run(Profile("Unidade excluída")); var before = JsonSerializer.Serialize(run, Serialization.Options);
        using var fixture = new Fixture([], [run]); var model = fixture.Model;
        var choice = model.PendingVisits.Single();
        Check(choice.Label.Contains("perfil excluído"), "Perfil excluído não foi identificado.");
        model.SelectedPendingVisit = choice;
        Check(model.ActiveProfile is null && model.ExecuteCommand.CanExecute(null), "Retomada exigiu um perfil cadastrado.");
        Check(!model.CanChooseFloors, "Dados originais permitiram edição.");
        Check(before == JsonSerializer.Serialize(run, Serialization.Options), "Seleção alterou a execução salva.");
    }

    private static void OriginalData()
    {
        var profile = Profile(); var run = Run(profile);
        var empty = ProfileValidation.CreateRun(profile, new AppSettings()); empty.CreatedAt = run.CreatedAt.AddHours(1);
        var before = JsonSerializer.Serialize(run, Serialization.Options);
        profile.Unit = "Unidade alterada"; profile.Floors[0].Resolution = "Texto alterado";
        using var fixture = new Fixture([profile], [empty, run]); var model = fixture.Model;
        Check(model.SelectedRun == run, "Uma tentativa vazia ocultou a visita com chamados.");
        Check(before == JsonSerializer.Serialize(run, Serialization.Options), "Seleção alterou os dados originais da retomada.");
        Check(ReferenceEquals(model.Floors![0], run.Floors[0].Floor), "Mostrou pavimentos de outra visita.");
    }

    private static void ProfileAndBusy()
    {
        var first = Profile(); var second = Profile("Unidade B"); var run = Run(first);
        using var fixture = new Fixture([first, second], [run]); var model = fixture.Model;
        model.ActiveProfile = second;
        Check(model.SelectedRun is null && model.ExecuteLabel == "Executar visita preventiva", "Trocar de perfil manteve uma visita de outra unidade selecionada.");
        Check(model.HasPendingVisits, "Pendências de outros perfis ficaram inacessíveis.");
        Select(model, run);
        Check(model.ActiveProfile == first, "Retomada não identificou o perfil selecionado.");
        model.State = ScreenState.Running;
        Check(!model.IsUnlocked && !model.ExecuteCommand.CanExecute(null), "Execução permitiu trocar a visita em andamento.");
        Check(!model.CheckAccessCommand.CanExecute(null) && model.StopCommand.CanExecute(null) && model.IsStopVisible,
            "O botão de parar ficou indisponível junto com as outras ações durante a execução.");
        model.State = ScreenState.Idle;
        Check(!model.IsStopVisible && !model.StopCommand.CanExecute(null), "O botão de parar permaneceu disponível depois da execução.");
        Check(model.SelectedRun == run && model.ExecuteCommand.CanExecute(null) && !model.CanChooseFloors, "Desbloquear a tela perdeu a retomada ou permitiu editar seus dados.");
    }

    private static void CompletedVisit()
    {
        var profile = Profile(); var run = Run(profile);
        using var fixture = new Fixture([profile], [run]); var model = fixture.Model;
        run.State = RunState.Completed; model.RefreshPendingVisits();
        Check(model.SelectedRun is null && !model.HasPendingVisits, "Visita concluída continuou disponível para retomada.");
        Check(model.ExecuteCommand.CanExecute(null), "Conclusão impediu uma nova visita.");
        Check(model.CanChooseFloors, "Nova visita não permitiu selecionar pavimentos.");
    }

    private static void HistoryIsReadOnly()
    {
        var profile = Profile(); var first = Run(profile); var second = Run(profile, "SD-1002");
        using var fixture = new Fixture([profile], [first, second]); var model = fixture.Model;
        Select(model, first);
        var before = JsonSerializer.Serialize(new[] { first, second }, Serialization.Options);
        model.SelectedHistoryRun = second;
        Check(model.SelectedRun == first && before == JsonSerializer.Serialize(new[] { first, second }, Serialization.Options), "Consulta ao histórico alterou a retomada.");
    }

    private static void CancelHistoryCleanup()
    {
        var profile = Profile(); var run = Run(profile);
        using var fixture = new Fixture([profile], [run], preview: false); var model = fixture.Model;
        Await(model.Store.SaveRunAsync(run)); var before = File.ReadAllBytes(RunFile(model, run));
        string? message = null; fixture.Dialogs.ConfirmDangerAnswer = text => { message = text; return false; };
        Await(model.ClearHistoryAsync());
        Check(message is not null && message.Contains("1 visita(s)") && message.Contains("1 pendente(s)") && message.Contains("logs e capturas") && message.Contains("Jira"), "Confirmação omitiu o alcance da exclusão.");
        Check(before.SequenceEqual(File.ReadAllBytes(RunFile(model, run))) && model.SelectedRun == run, "Cancelar alterou os dados.");
    }

    private static void ClearHistory()
    {
        var profile = Profile(); var deleted = Run(Profile("Perfil excluído")); var completed = Run(profile); completed.State = RunState.Completed;
        using var fixture = new Fixture([profile], [deleted, completed], preview: false); var model = fixture.Model;
        Await(model.Store.SaveRunAsync(deleted)); Await(model.Store.SaveRunAsync(completed));
        Select(model, deleted);
        fixture.Dialogs.ConfirmDangerAnswer = _ => true;
        Await(model.ClearHistoryAsync());
        Check(model.History.Count == 0 && model.HistoryFloors is null && model.SelectedRun is null, "Limpeza deixou dados nas tabelas.");
        Check(!model.HasPendingVisits && model.Progress == 0, "Limpeza deixou pendências ou progresso.");
        Check(model.ActiveProfile == profile && model.ExecuteCommand.CanExecute(null) && model.ExecuteLabel == "Executar visita preventiva", "Limpeza não restaurou o perfil e a nova execução.");
        Check(model.ClearHistoryCommand.CanExecute(null) && model.HistoryStatus.Contains("removidos") && model.HistoryStatusTone == StatusTone.Success, "Limpeza não informou sucesso ou liberou a tela.");
    }

    private static void CleanupGuards()
    {
        var profile = Profile(); var run = Run(profile); var confirmations = 0;
        using var fixture = new Fixture([profile], [run], preview: false); var model = fixture.Model;
        fixture.Dialogs.ConfirmDangerAnswer = _ => { confirmations++; return true; };
        foreach (var (name, block, release) in new (string, Action, Action)[]
        {
            ("carregamento", () => model.State = ScreenState.Loading, () => model.State = ScreenState.Idle),
            ("limpeza", () => model.State = ScreenState.Cleaning, () => model.State = ScreenState.Idle),
            ("execução", () => model.State = ScreenState.Running, () => model.State = ScreenState.Idle),
            ("exportação", () => model.IsExporting = true, () => model.IsExporting = false),
            ("prévia", () => model.IsPreview = true, () => model.IsPreview = false)
        })
        {
            block();
            Check(!model.ClearHistoryCommand.CanExecute(null), "Limpeza habilitada durante " + name);
            Await(model.ClearHistoryAsync());
            if (name == "limpeza")
                Check(!model.ExecuteCommand.CanExecute(null) && !model.ExportLogCommand.CanExecute(null) && !model.IsStopVisible, "Limpeza não bloqueou ações ou exibiu Parar.");
            release();
        }
        Check(confirmations == 0, "Limpeza aceitou uma operação bloqueada.");
        Check(model.ClearHistoryCommand.CanExecute(null) && model.SelectedRun == run, "Bloqueio alterou a seleção ou não liberou a limpeza.");
    }

    private static void PartialHistoryCleanup()
    {
        var profile = Profile(); var first = Run(profile); first.Id = "00000000000000000000000000000001";
        var second = Run(profile, "SD-1002"); second.Id = "00000000000000000000000000000002";
        using var fixture = new Fixture([profile], [first, second], preview: false); var model = fixture.Model;
        Await(model.Store.SaveRunAsync(first)); Await(model.Store.SaveRunAsync(second));
        fixture.Dialogs.ConfirmDangerAnswer = _ => true;
        using (File.Open(RunFile(model, second), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Await(model.ClearHistoryAsync());
            Check(model.HistoryStatus.Contains("incompleta") && model.HistoryStatusTone == StatusTone.Danger && model.History.Count == 1 && model.History[0].Id == second.Id,
                "Falha parcial declarou sucesso ou mostrou registros incorretos.");
        }
        Await(model.ClearHistoryAsync());
        Check(model.History.Count == 0, "Falha parcial impediu nova tentativa.");
    }

    private static void RunStatusPanel()
    {
        var first = Profile(); var second = Profile("Unidade B"); var run = Run(first);
        run.State = RunState.Failed; run.Message = "O campo “Andar” não corresponde a Pavimento 1.";
        using var fixture = new Fixture([first, second], [run]); var model = fixture.Model;
        Check(model.SelectedRun == run && model.HasStatus && model.Status == run.Message && model.StatusTone == StatusTone.Danger,
            "O motivo da falha da visita pendente não apareceu em vermelho.");
        run.State = RunState.Running; run.Message = "Faça login no Jira na janela do Edge. A execução continuará após o login.";
        model.ShowProgress(run);
        Check(model.Status == run.Message && model.StatusTone == StatusTone.Text, "O progresso da execução não atualizou o status.");
        Check(model.ProgressCount == "0 de 1 fechados", "A contagem de pavimentos fechados não apareceu no painel.");
        run.Floors[0].Stage = FloorStage.Closed; model.ShowProgress(run);
        Check(model.ProgressCount == "1 de 1 fechados" && model.Progress == 100, "A contagem não acompanhou o progresso.");
        run.Floors[0].Stage = FloorStage.Created;
        run.State = RunState.Interrupted; run.Message = "Execução interrompida. O progresso foi salvo.";
        model.ShowProgress(run);
        Check(model.Status == run.Message && model.StatusTone == StatusTone.Attention, "A interrupção não foi destacada.");
        model.State = ScreenState.Running;
        Check(model.StopCommand.CanExecute(null) && model.StopLabel == "Parar execução", "O botão de parar não ficou disponível.");
        model.Stop();
        Check(model.Status.StartsWith("Parando") && model.StatusTone == StatusTone.Muted, "Parar não informou a interrupção segura.");
        Check(model.StopLabel == "Parando…" && !model.StopCommand.CanExecute(null) && model.IsStopVisible, "O botão de parar não indicou a interrupção em andamento.");
        model.State = ScreenState.Idle;
        Check(model.StopLabel == "Parar execução" && !model.IsStopVisible, "O botão de parar não voltou ao estado inicial.");
        model.ActiveProfile = second;
        Check(model.SelectedRun is null && !model.HasStatus && model.ProgressCount.Length == 0 && !model.HasStatusRow, "O status de outra visita continuou visível.");
    }

    private static void DiscardVisit()
    {
        var profile = Profile(); var other = Profile("Unidade B");
        var empty = ProfileValidation.CreateRun(profile, new AppSettings()); empty.State = RunState.Failed; empty.Message = "O formulário não carregou.";
        var withIssue = Run(other);
        using var fixture = new Fixture([profile, other], [empty, withIssue], preview: false); var model = fixture.Model;
        Await(model.Store.SaveRunAsync(empty)); Await(model.Store.SaveRunAsync(withIssue));
        Check(model.SelectedRun == empty && model.CanDiscard, "A visita sem chamados não ofereceu o descarte.");
        Select(model, withIssue);
        Check(!model.CanDiscard, "A visita com chamado ofereceu o descarte.");
        fixture.Dialogs.ConfirmDangerAnswer = _ => throw new Exception("Pediu confirmação para uma visita com chamado.");
        Await(model.DiscardAsync());
        Select(model, empty);
        model.State = ScreenState.Running;
        Check(!model.DiscardCommand.CanExecute(null), "O descarte ficou disponível durante a execução.");
        model.State = ScreenState.Idle;
        string? message = null; fixture.Dialogs.ConfirmDangerAnswer = text => { message = text; return false; };
        Await(model.DiscardAsync());
        Check(message is not null && message.Contains("Nenhum chamado") && message.Contains("não pode ser desfeita") && model.SelectedRun == empty &&
            File.Exists(RunFile(model, empty)), "Cancelar o descarte alterou a visita ou a confirmação omitiu o alcance.");
        fixture.Dialogs.ConfirmDangerAnswer = _ => true;
        Await(model.DiscardAsync());
        Check(!File.Exists(RunFile(model, empty)) && File.Exists(RunFile(model, withIssue)), "O descarte não apagou somente a visita escolhida.");
        Check(model.History.Single() == withIssue, "O histórico manteve a visita descartada.");
        Check(model.SelectedRun is null && model.ActiveProfile == profile && !model.CanDiscard &&
            model.ExecuteCommand.CanExecute(null) && model.ExecuteLabel == "Executar visita preventiva", "O perfil não voltou a permitir uma nova visita.");
        Check(model.Status.Contains("descartada") && model.StatusTone == StatusTone.Success, "O descarte não foi informado no painel de status.");
    }

    private static void HistoryMessage()
    {
        var profile = Profile(); var failed = Run(profile); failed.State = RunState.Failed; failed.Message = "O Jira recusou o envio do formulário.";
        var completed = Run(Profile("Unidade B")); completed.State = RunState.Completed; completed.Message = "Visita concluída.";
        using var fixture = new Fixture([profile], [failed, completed]); var model = fixture.Model;
        model.SelectedHistoryRun = failed;
        Check(model.HasHistoryMessage && model.HistoryMessage == failed.Message && model.HistoryMessageTone == StatusTone.Danger,
            "O motivo da falha não apareceu em vermelho no Histórico.");
        model.SelectedHistoryRun = completed;
        Check(model.HistoryMessage == completed.Message && model.HistoryMessageTone == StatusTone.Success && model.HistoryFloors!.Single() == completed.Floors[0],
            "A mensagem não acompanhou a visita selecionada.");
        model.SelectedHistoryRun = null;
        Check(!model.HasHistoryMessage && model.HistoryFloors is null, "A mensagem ficou visível sem visita selecionada.");
    }

    private static void IncompleteProfile()
    {
        var profile = Profile(); profile.ReporterName = ""; profile.Extension = " ";
        using var fixture = new Fixture([profile], [], preview: false); var model = fixture.Model;
        Await(model.ExecuteAsync());
        var lines = model.Status.Split(Environment.NewLine);
        Check(model.HasStatus && model.StatusTone == StatusTone.Danger && lines.Contains("Preencha Solicitante.") && lines.Contains("Preencha Ramal."),
            "Os erros de validação não apareceram no painel, um por linha.");
        Check(fixture.Dialogs.Messages.Count == 0, "A validação abriu uma janela.");
        Check(model.History.Count == 0 && model.ExecuteCommand.CanExecute(null) && model.State == ScreenState.Idle &&
            (!Directory.Exists(Path.Combine(model.Store.Root, "runs")) || !Directory.EnumerateFiles(Path.Combine(model.Store.Root, "runs")).Any()),
            "Um perfil incompleto criou uma visita ou bloqueou a tela.");
    }

    private static void AccessCheck()
    {
        Func<AppSettings, Action<string>, CancellationToken, Task> connect = (_, report, _) => { report("Faça login no Jira na janela do Edge."); return Task.CompletedTask; };
        var reports = new List<string>();
        using var fixture = new Fixture([Profile()], [], preview: false, automation: _ => new FakeAutomation((settings, report, token) =>
            connect(settings, message => { reports.Add(message); report(message); }, token)));
        var model = fixture.Model;
        Await(model.CheckAccessAsync());
        Check(reports.Count == 1 && model.Status.StartsWith("Acesso confirmado") && model.StatusTone == StatusTone.Success && model.State == ScreenState.Idle,
            "O acesso confirmado não apareceu no painel.");
        connect = (_, _, _) => throw new Exception("O Edge não abriu.");
        Await(model.CheckAccessAsync());
        Check(model.Status.Contains("O Edge não abriu.") && model.StatusTone == StatusTone.Danger && fixture.Dialogs.Messages.Count == 0,
            "A falha do acesso não ficou no painel, em vermelho e sem janela.");
        connect = (_, _, token) => Task.Delay(Timeout.Infinite, token);
        var waiting = model.CheckAccessAsync();
        Check(model.State == ScreenState.Running && model.StopCommand.CanExecute(null), "A verificação não ofereceu Parar.");
        model.Stop(); Await(waiting);
        Check(model.Status == "Verificação de acesso interrompida." && model.StatusTone == StatusTone.Attention && model.State == ScreenState.Idle,
            "A interrupção da verificação não foi informada.");
    }

    private static void SettingsValidation()
    {
        using var fixture = new Fixture([Profile()], [], preview: false); var model = fixture.Model;
        model.JiraUrl = "http://jira.example.com";
        Await(model.SaveSettingsAsync());
        Check(model.SettingsStatus.Contains("https://") && model.SettingsStatusTone == StatusTone.Danger && !File.Exists(Path.Combine(model.Store.Root, "settings.json")),
            "Um endereço inválido foi aceito.");
        model.JiraUrl = "https://jira.example.com/";
        Check(model.SettingsStatus.Length == 0, "Editar o endereço não limpou o aviso.");
        Await(model.SaveSettingsAsync());
        var saved = Await(model.Store.LoadSettingsAsync());
        Check(model.SettingsStatus == "Configurações salvas." && model.SettingsStatusTone == StatusTone.Success && saved.JiraUrl == "https://jira.example.com",
            "O endereço corrigido não foi salvo sem a barra final.");
    }

    private static void ThemeSwitch()
    {
        // O App destes testes inicia como o aplicativo real e aplica o tema salvo neste computador; o teste parte do claro.
        Theme.Apply(AppTheme.Light);
        using var fixture = new Fixture([Profile()], [], preview: false); var model = fixture.Model;
        var window = Open(model);
        object Resource(string key) => Application.Current.Resources[key];
        try
        {
            window.SelectPage(3);
            model.JiraUrl = "http://jira.example.com"; Await(model.SaveSettingsAsync());
            var status = Control<TextBlock>(window, "SettingsStatus"); var lightDanger = Resource("AppDangerTextBrush");
            Check(Theme.Current == AppTheme.Light && window.Background == Resource("AppCanvasBackgroundBrush") && status.Foreground == lightDanger,
                "A janela não abriu no tema claro.");
            Control<CheckBox>(window, "DarkThemeBox").IsChecked = true;
            Check(model.IsDarkTheme && Theme.Current == AppTheme.Dark, "Marcar o tema escuro não chegou ao ViewModel.");
            Check(window.Background == Resource("AppCanvasBackgroundBrush") && ((SolidColorBrush)window.Background).Color.R < 0x30 && status.Foreground == Resource("AppDangerTextBrush") && status.Foreground != lightDanger,
                "A janela e a cor de estado não acompanharam o tema escuro.");
            Screenshot(window, "configuracoes-escuro");
            var editor = new ProfileWindow(Profile());
            Check(Control<TextBox>(editor, "NameBox").Background == Resource("AppInputBackgroundBrush"), "O editor de perfil não abriu no tema escuro.");
            Screenshot(editor, "editar-perfil-escuro"); editor.Close();
            Await(model.ThemeSaved);
            Check(Await(model.Store.LoadSettingsAsync()).Theme == AppTheme.Dark, "O tema escuro não foi salvo.");
            Check(!File.ReadAllText(Path.Combine(model.Store.Root, "settings.json")).Contains("http://jira.example.com"),
                "Salvar o tema gravou o endereço inválido ainda não salvo.");
            model.IsDarkTheme = false; Await(model.ThemeSaved);
            Check(Theme.Current == AppTheme.Light && Await(model.Store.LoadSettingsAsync()).Theme == AppTheme.Light, "A volta ao tema claro não valeu.");
        }
        finally { window.Close(); Theme.Apply(AppTheme.Light); }
    }

    private static void DangerConfirmDefaults()
    {
        var dialog = new DangerConfirmWindow("Limpar histórico e pendências", "Apagar todo o histórico deste computador?", "Limpar tudo");
        try
        {
            var buttons = Descendants(dialog).OfType<Button>().ToArray();
            Check(buttons.Single(b => b.IsDefault).Content.Equals("Cancelar") && buttons.Single(b => b.IsCancel).Content.Equals("Cancelar"), "Confirmação não usa Cancelar por padrão.");
            Screenshot(dialog, "confirmar-limpeza");
        }
        finally { dialog.Close(); }
    }

    private static void TitleBar()
    {
        Theme.Apply(AppTheme.Light);
        using var fixture = new Fixture([Profile()], [], preview: false); var model = fixture.Model;
        var window = Open(model);
        T Part<T>(string name) => (T)window.Template.FindName(name, window);
        try
        {
            var theme = Part<Button>("ThemeButton");
            Check(window.ShowThemeToggle && theme.Visibility == Visibility.Visible && Part<Button>("CloseButton").Visibility == Visibility.Visible &&
                Part<Button>("RestoreButton").Visibility == Visibility.Collapsed && string.IsNullOrEmpty(window.Subtitle),
                "A barra de título da janela principal não mostrou a troca de tema e os botões da janela.");
            Click(theme);
            Check(JanoWindow.ToggleThemeCommand.CanExecute(null, theme) && model.IsDarkTheme && Theme.Current == AppTheme.Dark, "O botão de tema da barra não trocou o tema.");
            Await(model.ThemeSaved);
            Check(Await(model.Store.LoadSettingsAsync()).Theme == AppTheme.Dark, "O tema escolhido na barra não foi salvo.");
            Screenshot(window, "barra-escuro");
            Click(theme); Await(model.ThemeSaved);
            Check(!model.IsDarkTheme && Theme.Current == AppTheme.Light, "O botão de tema da barra não voltou ao claro.");
            model.State = ScreenState.Running;
            Check(window.IsBusy, "O emblema não acompanhou o trabalho em andamento.");
            model.State = ScreenState.Idle;
            Check(!window.IsBusy, "O emblema continuou ativo depois do trabalho.");
        }
        finally { window.Close(); Theme.Apply(AppTheme.Light); }
        var dialog = new DangerConfirmWindow("Limpar histórico", "Apagar?", "Limpar tudo") { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            dialog.Show();
            var parts = new[] { "ThemeButton", "MinimizeButton", "MaximizeButton" }.Select(n => (Button)dialog.Template.FindName(n, dialog));
            Check(dialog.Subtitle == "Limpar histórico" && parts.All(b => b.Visibility == Visibility.Collapsed),
                "A confirmação mostrou botões de tema ou de tamanho, ou ficou sem subtítulo.");
        }
        finally { dialog.Close(); }
    }

    private static void MessageDefaults()
    {
        var retry = new MessageWindow("Tentar o fechamento de novo?", "Conferir fechamento pendente", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        try
        {
            var buttons = retry.Buttons;
            Check(buttons.Select(b => b.Content).SequenceEqual(["Sim", "Não"]) && buttons.Single(b => b.IsDefault).Content.Equals("Não") &&
                buttons.Single(b => b.IsCancel).Content.Equals("Não") && buttons[1].Style == Application.Current.Resources["Primary"] &&
                retry.Result == MessageBoxResult.No && retry.Subtitle == "Conferir fechamento pendente",
                "A pergunta de nova tentativa não manteve Não como padrão e recusa.");
            Screenshot(retry, "mensagem-pergunta");
        }
        finally { retry.Close(); }
        var warning = new MessageWindow("Aviso.", "", MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.None);
        try
        {
            Check(warning.Buttons.Single() is { IsDefault: true, IsCancel: true } ok && ok.Content.Equals("OK") && warning.Result == MessageBoxResult.OK &&
                warning.Title == "Vistora", "O aviso não ficou só com OK, padrão e recusa.");
        }
        finally { warning.Close(); }
        var discard = new MessageWindow("Descartar?", "Descartar alterações", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.None);
        try
        {
            Check(discard.Buttons.Single(b => b.IsDefault).Content.Equals("Sim") && discard.Buttons.Single(b => b.IsCancel).Content.Equals("Cancelar") &&
                discard.Result == MessageBoxResult.Cancel, "Sim, Não e Cancelar não seguiram o padrão da MessageBox.");
        }
        finally { discard.Close(); }
    }

    private static void ProfileEditorValidation()
    {
        var profile = Profile(); profile.Floors.Add(new Floor { Name = "Térreo" }); profile.Floors.Add(new Floor { Name = " pavimento 1 " });
        var editor = new ProfileWindow(profile) { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        try
        {
            editor.Show(); editor.UpdateLayout();
            var message = (TextBlock)editor.FindName("ValidationMessage"); var tabs = (TabControl)editor.FindName("EditorTabs");
            var name = (TextBox)editor.FindName("NameBox"); var grid = (DataGrid)editor.FindName("FloorsGrid");
            var red = Application.Current.Resources["AppDangerBrush"];
            name.Text = " "; tabs.SelectedIndex = 1; editor.Save(editor, new RoutedEventArgs());
            Check(tabs.SelectedIndex == 0 && name.BorderBrush == red && message.Text.Contains("nome do perfil"), "O nome vazio não foi marcado no editor.");
            Screenshot(editor, "perfil-invalido");
            name.Text = "Unidade A";
            Check(message.Text.Length == 0 && name.BorderBrush != red, "A marcação continuou depois da correção.");
            editor.Save(editor, new RoutedEventArgs());
            Check(tabs.SelectedIndex == 1 && grid.BorderBrush == red && grid.SelectedItem == editor.Profile.Floors[0] && message.Text.Contains("pavimento"),
                "O pavimento repetido não foi marcado no editor.");
        }
        finally { editor.Close(); }
    }

    private static void ProfileEditorUnsavedChanges()
    {
        ProfileWindow Open(Func<bool> confirm)
        {
            var editor = new ProfileWindow(Profile()) { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ConfirmDiscard = confirm };
            editor.Show(); editor.UpdateLayout(); return editor;
        }
        var asked = 0;
        var untouched = Open(() => { asked++; return false; });
        untouched.Cancel(untouched, new RoutedEventArgs());
        Check(!untouched.IsVisible && asked == 0, "Cancelar sem alterações pediu confirmação ou não fechou.");
        var edited = Open(() => { asked++; return false; });
        try
        {
            ((TextBox)edited.FindName("NameBox")).Text = "Unidade renomeada";
            edited.Cancel(edited, new RoutedEventArgs());
            Check(edited.IsVisible && asked == 1, "Cancelar com alterações fechou sem confirmação.");
            ((TextBox)edited.FindName("NameBox")).Text = "Unidade A";
            edited.Cancel(edited, new RoutedEventArgs());
            Check(!edited.IsVisible && asked == 1, "Desfazer a alteração ainda pediu confirmação.");
        }
        finally { edited.Close(); }
        var floors = Open(() => { asked++; return true; });
        floors.AddFloor(floors, new RoutedEventArgs());
        floors.Close();
        Check(!floors.IsVisible && asked == 2, "Fechar a janela depois de incluir um pavimento não pediu confirmação.");
    }

    // O mesmo caminho de um clique: o botão executa o próprio comando a partir de si. A automação enfileira o clique,
    // então a fila da interface é esvaziada antes de seguir.
    private static void Click(Button button)
    {
        ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(button)
            .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
    private static void Screenshot(Window window, string name)
    {
        if (screenshotDirectory is null) return;
        Directory.CreateDirectory(screenshotDirectory);
        if (!window.IsVisible)
        {
            window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10000; window.Top = -10000; window.Show();
        }
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(screenshotDirectory, name + ".png")); encoder.Save(file);
    }
    private static void Await(Task task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.Result; }

    // Respostas das janelas definidas por cada teste; uma janela inesperada aparece como mensagem registrada.
    private sealed class TestDialogs : IMainDialogs
    {
        public Func<string, bool> ConfirmDangerAnswer { get; set; } = _ => throw new Exception("Confirmação inesperada.");
        public List<string> Messages { get; } = [];
        public bool ConfirmDanger(string title, string message, string confirmLabel) => ConfirmDangerAnswer(message);
        public bool Confirm(string message) { Messages.Add(message); return false; }
        public VisitProfile? EditProfile(VisitProfile profile) => null;
        public string? AskIssueKey(string floorName) => null;
        public bool ConfirmCloseRetry(FloorRun floor, IssueSnapshot snapshot) => false;
        public void ShowWarning(string message) => Messages.Add(message);
        public void ShowError(string message) => Messages.Add(message);
        public Task ExportLogAsync(VisitRun? run) => Task.CompletedTask;
    }

    // Só a verificação de acesso usa a automação nestes testes; o fluxo completo é coberto pelos testes do Edge.
    private sealed class FakeAutomation(Func<AppSettings, Action<string>, CancellationToken, Task> connect) : IJiraAutomation
    {
        public Task ConnectAsync(AppSettings settings, Action<string> report, CancellationToken cancellationToken, string? initialIssueKey = null) =>
            connect(settings, report, cancellationToken);
        public Task PrepareCreateAsync(VisitRun run, FloorRun floor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> SubmitCreateAsync(VisitRun run, FloorRun floor) => throw new NotSupportedException();
        public Task<IssueSnapshot> ReadIssueAsync(VisitRun run, FloorRun floor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AssignAsync(VisitRun run, FloorRun floor) => throw new NotSupportedException();
        public Task StartAsync(VisitRun run, FloorRun floor) => throw new NotSupportedException();
        public Task PrepareCloseAsync(VisitRun run, FloorRun floor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SubmitCloseAsync(VisitRun run, FloorRun floor) => throw new NotSupportedException();
        public Task CaptureFailureAsync(VisitRun run) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DiagnosticService diagnostics;
        public TestDialogs Dialogs { get; } = new();
        public MainViewModel Model { get; }
        public Fixture(List<VisitProfile> profiles, List<VisitRun> runs, bool preview = true, Func<LocalStore, IJiraAutomation>? automation = null)
        {
            diagnostics = new DiagnosticService(Path.Combine(Path.GetTempPath(), "VistoraDesktopTests", Guid.NewGuid().ToString("N")));
            Model = new MainViewModel(diagnostics, Dialogs, preview, automationFactory: automation);
            Model.Show(new ProfileDocument { Profiles = profiles, ActiveProfileId = profiles.FirstOrDefault()?.Id }, runs);
        }
        public void Dispose() => Await(diagnostics.DisposeAsync().AsTask());
    }
}
