using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Playwright;
using Vistora.Core;
using Vistora.Infrastructure;

namespace Vistora.Automation.Edge;

public sealed class EdgeJiraAutomation(LocalStore store) : IJiraAutomation
{
    private IPlaywright? playwright;
    private IBrowserContext? context;
    private IPage? page;
    private DomControls? controls;
    private ILocator? closingDialog;
    private AppSettings settings = new();
    private DiagnosticAttempt? diagnostics;
    public void SetDiagnostics(DiagnosticAttempt value) => diagnostics = value;
    private IPage Page => page ?? throw new InvalidOperationException("O navegador não está conectado.");
    private DomControls Ui => controls ?? throw new InvalidOperationException("O navegador não está conectado.");
    private JiraWorkflow Workflow => settings.Jira;
    private string[] Labels(string field) => JiraFields.Labels(settings, field);
    private static readonly Regex IssueKeyPattern = new(@"\b([A-Z][A-Z0-9]*-\d+)\b");
    private static readonly TimeSpan SubmitStablePeriod = TimeSpan.FromMilliseconds(300);
    // Chamados com todos os campos já conferidos nesta tentativa, com a visita e o pavimento correspondentes.
    private readonly Dictionary<string, string> verifiedIssues = [];
    // Cada tipo de problema de conferência é capturado uma vez por tentativa.
    private readonly HashSet<string> capturedMoments = [];

    public async Task ConnectAsync(AppSettings appSettings, Action<string> report, CancellationToken cancellationToken, string? initialIssueKey = null)
    {
        settings = appSettings;
        playwright = await Playwright.CreateAsync();
        try
        {
            context = await playwright.Chromium.LaunchPersistentContextAsync(store.BrowserDirectory,
                new() { Channel = "msedge", Headless = false, ChromiumSandbox = true, ViewportSize = new() { Width = 1440, Height = 960 } });
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException("Não foi possível abrir o navegador de automação. Confira a instalação do navegador e se a máquina permite abrir sua janela.", ex);
        }
        page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        page.SetDefaultTimeout(settings.TimeoutSeconds * 1000);
        page.SetDefaultNavigationTimeout(settings.TimeoutSeconds * 1000);
        controls = new DomControls(page, settings.SelectorOverrides, diagnostics);
        if (diagnostics is not null && store.Diagnostics is not null)
            store.Diagnostics.UpdateBrowserVersion(diagnostics.Id, context.Browser?.Version,
                typeof(Playwright).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? typeof(Playwright).Assembly.GetName().Version?.ToString());
        page.RequestFailed += (_, request) => diagnostics?.Event("navigation.failed", "Uma requisição do navegador falhou.",
            DiagnosticLevel.Warning, new() { ["pageKind"] = PageKind(request.Url), ["resourceType"] = request.ResourceType });
        page.Response += (_, response) =>
        {
            if (response.Status >= 400) diagnostics?.Event("navigation.http_error", "Resposta HTTP de erro recebida.", DiagnosticLevel.Warning,
                new() { ["status"] = response.Status, ["pageKind"] = PageKind(response.Url) });
        };
        var initialUrl = initialIssueKey is null ? settings.PortalUrl : IssueUrl(initialIssueKey);
        await NavigateAsync(initialUrl);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(settings.LoginTimeoutMinutes);
        DateTimeOffset? formLoadingSince = null;
        string? previousMessage = null;
        string? waitingKind = null;
        var waitingSince = Stopwatch.GetTimestamp();
        diagnostics?.RecordDuration("browser.login_wait", 0);
        diagnostics?.RecordDuration("browser.form_loading", 0);
        void WaitChanged(string? kind)
        {
            if (kind == waitingKind) return;
            if (waitingKind is not null)
            {
                var duration = Stopwatch.GetElapsedTime(waitingSince).TotalMilliseconds;
                diagnostics?.RecordDuration(waitingKind == "login" ? "browser.login_wait" : "browser.form_loading", duration);
                diagnostics?.Event("browser.wait_finished", "Espera concluída.", details: new() { ["waitKind"] = waitingKind }, durationMs: duration);
            }
            waitingKind = kind; waitingSince = Stopwatch.GetTimestamp();
            if (kind is not null) diagnostics?.Event("browser.wait_started", "Aguardando o navegador.", details: new() { ["waitKind"] = kind });
        }
        try
        {
        while (!(initialIssueKey is null ? await PortalReadyAsync() : await IssueReadyAsync(initialUrl)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var onForm = IsTargetUrl(Page.Url, initialUrl);
            WaitChanged(onForm ? "form_loading" : "login");
            string message;
            if (onForm)
            {
                formLoadingSince ??= DateTimeOffset.UtcNow;
                message = initialIssueKey is null ? "Carregando o formulário de visita preventiva…" : $"Carregando o chamado {initialIssueKey} para retomar…";
                if (DateTimeOffset.UtcNow - formLoadingSince > TimeSpan.FromSeconds(settings.TimeoutSeconds))
                {
                    var timeoutError = new InvalidOperationException(initialIssueKey is null
                        ? "O formulário de visita preventiva abriu, mas seus campos ainda não foram reconhecidos. Confira a página e o diagnóstico da execução."
                        : $"O chamado {initialIssueKey} abriu, mas seu status ainda não carregou. Retome pela aba Executar visita quando estiver pronto.");
                    timeoutError.Data["DiagnosticCode"] = "FORM_LOAD_TIMEOUT"; throw timeoutError;
                }
            }
            else
            {
                formLoadingSince = null;
                message = "Faça login no Jira na janela do Edge. A execução continuará após o login.";
            }
            if (message != previousMessage) { report(message); previousMessage = message; }
            if (DateTimeOffset.UtcNow > deadline)
            { var timeoutError = new InvalidOperationException("O tempo para login terminou. Retome a execução quando estiver pronto."); timeoutError.Data["DiagnosticCode"] = "LOGIN_TIMEOUT"; throw timeoutError; }
            await Task.Delay(600, cancellationToken);
        }
        }
        finally { WaitChanged(null); }
        diagnostics?.Event("browser.ready", "Página pronta para execução.");
        report(initialIssueKey is null ? "Login confirmado. Preparando os chamados." : $"Login confirmado. Retomando os chamados já registrados a partir de {initialIssueKey}.");
    }

    private static string PageKind(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "unknown";
        if (Regex.IsMatch(uri.AbsolutePath, "login|signin|auth", RegexOptions.IgnoreCase)) return "authentication";
        if (uri.AbsolutePath.Contains("/create/", StringComparison.Ordinal)) return "create_form";
        if (uri.AbsolutePath.Contains("/portal/", StringComparison.Ordinal)) return "customer_portal";
        if (uri.AbsolutePath.Contains("/issue/", StringComparison.Ordinal) || uri.AbsolutePath.Contains("/browse/", StringComparison.Ordinal)) return "issue";
        return "other";
    }
    private async Task NavigateAsync(string url)
    {
        var begin = Stopwatch.GetTimestamp();
        diagnostics?.Event("navigation.started", "Navegação iniciada.", DiagnosticLevel.Debug, new() { ["pageKind"] = PageKind(url) });
        await Page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        diagnostics?.Event("navigation.finished", "Documento carregado.", DiagnosticLevel.Debug,
            new() { ["pageKind"] = PageKind(url) }, durationMs: Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
    }

    private async Task<bool> IssueReadyAsync(string initialUrl)
    {
        if (Page.IsClosed) throw new InvalidOperationException("A janela do Edge foi fechada. Retome a execução para continuar.");
        if (!IsTargetUrl(Page.Url, initialUrl)) return false;
        return await DomControls.IsUniqueVisibleAsync(await StatusButtonLocatorAsync());
    }

    private async Task<bool> PortalReadyAsync()
    {
        if (Page.IsClosed) throw new InvalidOperationException("A janela do Edge foi fechada. Retome a execução para continuar.");
        if (!IsPortalUrl(Page.Url)) return false;
        if (settings.SelectorOverrides.TryGetValue("portal.ready", out var selector))
            return await DomControls.IsUniqueVisibleAsync(Page.Locator(selector).Filter(new() { Visible = true }));
        foreach (var (key, field) in new[] { ("portal.reporter", "reporter"), ("portal.title", "title"), ("portal.extension", "extension") })
        {
            var control = await Ui.TryFieldAsync(key, Labels(field));
            if (control is null || !await control.IsEnabledAsync()) return false;
        }
        return true;
    }

    private bool IsPortalUrl(string url) => IsTargetUrl(url, settings.PortalUrl);

    private bool IsTargetUrl(string url, string targetUrl) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Authority, new Uri(settings.JiraUrl).Authority, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.AbsolutePath.TrimEnd('/'), new Uri(targetUrl).AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);

    public async Task PrepareCreateAsync(VisitRun run, FloorRun floor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Page.Url != settings.PortalUrl) await NavigateAsync(settings.PortalUrl);
        // Inclui os envios de anexos, que continuam depois que o nome do arquivo aparece na página.
        using var mutations = new MutationTracker(Page, settings.JiraUrl);
        var profile = run.Profile;
        await Ui.ChooseAsync("portal.reporter", Labels("reporter"), profile.ReporterEmail,
            new Regex($"(?:^|[\\s(<]){Regex.Escape(profile.ReporterEmail)}(?=$|[\\s)>])", RegexOptions.IgnoreCase), profile.ReporterEmail);
        cancellationToken.ThrowIfCancellationRequested();
        var filled = new List<(ILocator, string)>();
        async Task FillTrackedAsync(string key, string[] labels, string value) => filled.Add((await Ui.FillAsync(key, labels, value), value));
        await FillTrackedAsync("portal.title", Labels("title"), profile.Title);
        await FillTrackedAsync("portal.extension", Labels("extension"), profile.Extension);
        await FillTrackedAsync("portal.fullName", Labels("fullName"), profile.FullName);
        await Ui.ChooseAsync("portal.unit", Labels("unit"), profile.Unit,
            new Regex($"^{Regex.Escape(profile.Unit)}$", RegexOptions.IgnoreCase), profile.Unit);
        await FillTrackedAsync("portal.floor", Labels("floor"), floor.Floor.Name);
        await FillTrackedAsync("portal.sector", Labels("sector"), floor.Floor.Sector);
        await FillTrackedAsync("portal.room", Labels("room"), floor.Floor.Room);
        await FillTrackedAsync("portal.phone", Labels("phone"), profile.Phone);
        await FillTrackedAsync("portal.description", Labels("description"), profile.Description);
        if (profile.Attachments.Count > 0)
        {
            diagnostics?.Event("attachments.prepared", "Anexos preparados.", details: new() { ["count"] = profile.Attachments.Count });
            var upload = settings.SelectorOverrides.TryGetValue("portal.attachments", out var selector)
                ? Page.Locator(selector) : Page.Locator("input[type=file]");
            if (await upload.CountAsync() != 1) throw new InvalidOperationException("Não foi possível identificar onde incluir os anexos.");
            await upload.SetInputFilesAsync(profile.Attachments.ToArray());
            foreach (var attachment in profile.Attachments)
                await Page.GetByText(Path.GetFileName(attachment), new() { Exact = true }).First.WaitForAsync();
            mutations.MarkActivity();
        }
        await WaitReadyToSubmitAsync("portal.submit", Labels("submit"), null, mutations, filled, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<string> SubmitCreateAsync(VisitRun run, FloorRun floor)
    {
        var oldUrl = Page.Url;
        await Ui.ClickAsync("portal.submit", Labels("submit"));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(settings.TimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Page.Url != oldUrl)
            {
                var match = IssueKeyPattern.Match(Page.Url);
                if (match.Success) return match.Groups[1].Value;
            }
            var links = Page.Locator("a[href*='/issue/'],a[href*='/browse/'],a[href*='/portal/']");
            var keys = new HashSet<string>();
            for (var i = 0; i < await links.CountAsync(); i++)
            {
                var link = links.Nth(i);
                if (!await link.IsVisibleAsync()) continue;
                var text = await link.InnerTextAsync();
                var match = IssueKeyPattern.Match(text);
                if (match.Success) keys.Add(match.Groups[1].Value);
            }
            if (keys.Count == 1 && Page.Url != oldUrl) return keys.Single();
            await Task.Delay(300);
        }
        throw new ReconciliationException($"O formulário de {floor.Name} foi enviado, mas o número do chamado não foi identificado. Confira o Jira e informe o número ao retomar pela aba Executar visita.");
    }

    private string IssueUrl(string key) => settings.JiraUrl.TrimEnd('/') +
        Workflow.IssuePath.Trim().Replace("{project}", key.Split('-')[0], StringComparison.Ordinal).Replace("{key}", key, StringComparison.Ordinal);

    // Os nomes são digitados em Configurações; espaços extras não fazem parte do nome no Jira.
    private static bool SameText(string actual, string expected) =>
        string.Equals(DomControls.Normalize(actual), DomControls.Normalize(expected), StringComparison.OrdinalIgnoreCase);
    private bool IsClosedStatus(string status) => Workflow.ClosedStatuses.Any(closed => SameText(status, closed));
    private Regex StatusNamePattern() => new($@"^({string.Join("|",
        new[] { Workflow.WaitingStatus, Workflow.InProgressStatus }.Concat(Workflow.ClosedStatuses).Select(s => Regex.Escape(DomControls.Normalize(s))))})" +
        @"(?:\s*-\s*(?:Change status|Alterar status))?$", RegexOptions.IgnoreCase);

    // Substitui a espera fixa antes de cada envio. O formulário precisa ficar estável por um instante:
    // sem gravações pendentes, com o botão de envio habilitado e os campos ainda com os valores preenchidos.
    private async Task WaitReadyToSubmitAsync(string key, string[] names, ILocator? scope, MutationTracker mutations,
        IReadOnlyList<(ILocator Field, string Value)> fields, CancellationToken cancellationToken)
    {
        var begin = Stopwatch.GetTimestamp();
        long? readySince = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = mutations.Settled && await Ui.ActionReadyAsync(key, names, scope);
            foreach (var (field, value) in fields)
            {
                if (!ready) break;
                ready = await DomControls.HasValueAsync(field, value);
            }
            if (!ready) readySince = null;
            else if (readySince is null) readySince = Stopwatch.GetTimestamp();
            else if (Stopwatch.GetElapsedTime(readySince.Value) >= SubmitStablePeriod)
            {
                diagnostics?.Event("submit.ready", "Formulário pronto para envio.", DiagnosticLevel.Debug,
                    new() { ["controlKey"] = key, ["mutationCount"] = mutations.Count }, durationMs: Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
                return;
            }
            await Task.Delay(150, cancellationToken);
        } while (Stopwatch.GetElapsedTime(begin) < TimeSpan.FromSeconds(settings.TimeoutSeconds));
        diagnostics?.Event("submit.not_ready", "O formulário não ficou pronto para envio.", DiagnosticLevel.Warning,
            new() { ["controlKey"] = key }, errorCode: "SUBMIT_NOT_READY", durationMs: Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
        var error = new InvalidOperationException($"O formulário não ficou pronto para “{names[0]}” no tempo esperado. Nada foi enviado; confira a página do Jira e retome pela aba Executar visita.");
        error.Data["DiagnosticCode"] = "SUBMIT_NOT_READY";
        throw error;
    }

    public async Task<IssueSnapshot> ReadIssueAsync(VisitRun run, FloorRun floor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = floor.IssueKey ?? throw new InvalidOperationException("Chamado ainda não identificado.");
        await NavigateAsync(IssueUrl(key));
        var title = settings.SelectorOverrides.TryGetValue("issue.title", out var titleSelector)
            ? Page.Locator(titleSelector) : Page.GetByRole(AriaRole.Heading, new() { Name = run.Profile.Title, Exact = true });
        await title.First.WaitForAsync(new() { State = WaitForSelectorState.Visible });
        if (DomControls.Normalize(await (await DomControls.UniqueAsync(title, "Título")).InnerTextAsync()) != DomControls.Normalize(run.Profile.Title))
            throw new InvalidOperationException("O título do chamado não corresponde à visita configurada.");
        var body = await Page.Locator("body").InnerTextAsync();
        if (!Regex.IsMatch(body, $"\\b{Regex.Escape(key)}\\b")) throw new InvalidOperationException("O número exibido não corresponde ao chamado registrado.");
        // Os dados de abertura não mudam durante a visita. Depois da primeira conferência completa
        // nesta tentativa, as leituras seguintes do mesmo pavimento conferem só status e responsável.
        var identity = $"{run.Id}/{floor.Floor.Id}";
        if (verifiedIssues.GetValueOrDefault(key) == identity)
            diagnostics?.Event("issue.fields_reused", "Campos de abertura já conferidos nesta tentativa.", DiagnosticLevel.Debug);
        else
        {
            await VerifyIssueFieldsAsync(run, floor, key);
            verifiedIssues[key] = identity;
        }
        var status = await StatusAsync(cancellationToken);
        var closed = IsClosedStatus(status);
        var started = SameText(status, Workflow.InProgressStatus);
        var assignee = await AssigneeAsync();
        // O Jira pode manter a ação "Assign to me" ao lado do responsável já salvo.
        var self = IsTechnician(assignee, run.TechnicianName);
        var resolutionMatches = false;
        var publicMatches = false;
        var teamMatches = false;
        if (closed)
        {
            teamMatches = string.Equals(DomControls.Normalize(await Ui.ValueAsync("issue.team", Labels("team"))),
                DomControls.Normalize(run.Settings.ClosingTeam), StringComparison.OrdinalIgnoreCase);
            if (!teamMatches) await CaptureMomentAsync(run, "equipe-divergente", "Captura da equipe divergente disponível.");
            resolutionMatches = await ResolutionMatchesAsync(run, floor.Floor.Resolution) ?? ResolutionVerifiedByVisit(floor);
            publicMatches = await PublicCommentMatchesAsync(run, key, floor.Floor.Resolution, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(key, status, self, closed, started, resolutionMatches, publicMatches, teamMatches);
    }

    private async Task VerifyIssueFieldsAsync(VisitRun run, FloorRun floor, string key)
    {
        foreach (var (field, labels, expected) in new (string, string[], string)[]
        {
            ("issue.floor", Labels("floor"), floor.Floor.Name), ("issue.unit", Labels("unit"), run.Profile.Unit),
            ("issue.sector", Labels("sector"), floor.Floor.Sector), ("issue.room", Labels("room"), floor.Floor.Room),
            ("issue.fullName", Labels("fullName"), run.Profile.FullName),
            ("issue.extension", Labels("extension"), run.Profile.Extension),
            ("issue.requestType", ["Request Type", "Tipo de solicitação"], Workflow.RequestType)
        })
        {
            var actual = await Ui.ValueAsync(field, labels);
            var matches = SameText(actual, expected);
            diagnostics?.Event("field.verified", "Campo conferido.", matches ? DiagnosticLevel.Debug : DiagnosticLevel.Warning,
                new() { ["controlKey"] = field, ["matches"] = matches });
            if (!matches)
                throw new InvalidOperationException($"{key}: o campo “{labels[0]}” não corresponde a {floor.Name}. Confira os dados antes de retomar.");
        }
        var reporter = await Ui.ValueAsync("issue.reporter", ["Reporter", "Relator", "Solicitante"]);
        if (!string.Equals(DomControls.NormalizePersonName(reporter), DomControls.NormalizePersonName(run.Profile.ReporterName), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{key}: o solicitante configurado não foi encontrado na página.");
    }

    private async Task<ILocator> StatusButtonAsync(CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(settings.TimeoutSeconds);
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var button = await StatusButtonLocatorAsync();
            var count = await button.CountAsync();
            if (count > 1)
            {
                diagnostics?.Event("control.ambiguous", "Mais de um controle de status foi encontrado.", DiagnosticLevel.Warning,
                    new() { ["controlKey"] = "issue.status", ["count"] = count }, errorCode: "FIELD_AMBIGUOUS");
                throw new InvalidOperationException("O Jira exibiu mais de um controle de status do chamado. Confira a página antes de retomar.");
            }
            if (count == 1 && await button.IsEnabledAsync()) return button;
            if (count == 0)
            {
                // Nas leituras que só conferem status e responsável, esta espera é quase todo o tempo da leitura.
                // O Playwright percebe o controle assim que ele aparece; o limite curto mantém o cancelamento rápido.
                try { await StatusCandidatesLocator().First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 500 }); }
                catch (TimeoutException) { }
                continue;
            }
            await Task.Delay(100, cancellationToken);
        } while (DateTimeOffset.UtcNow < deadline);
        throw new InvalidOperationException("O controle de status do chamado não carregou no tempo esperado. Confira a página e retome pela aba Executar visita.");
    }

    private async Task<ILocator> StatusButtonLocatorAsync()
    {
        ILocator button;
        if (settings.SelectorOverrides.TryGetValue("issue.status", out var selector)) button = Page.Locator(selector);
        else
        {
            button = Page.Locator("[data-testid='issue-field-status.ui.status-view.status-button.status-button']");
            if (await button.Filter(new() { Visible = true }).CountAsync() == 0)
                button = Page.GetByRole(AriaRole.Button, new() { NameRegex = StatusNamePattern() });
        }
        return button.Filter(new() { Visible = true });
    }

    // Qualquer controle visível que StatusButtonLocatorAsync possa escolher.
    private ILocator StatusCandidatesLocator() => (settings.SelectorOverrides.TryGetValue("issue.status", out var selector)
        ? Page.Locator(selector)
        : Page.Locator("[data-testid='issue-field-status.ui.status-view.status-button.status-button']")
            .Or(Page.GetByRole(AriaRole.Button, new() { NameRegex = StatusNamePattern() }))).Filter(new() { Visible = true });

    private async Task<string> StatusAsync(CancellationToken cancellationToken = default) =>
        DomControls.Normalize(await (await StatusButtonAsync(cancellationToken)).InnerTextAsync());

    public async Task AssignAsync(VisitRun run, FloorRun floor)
    {
        using var mutations = new MutationTracker(Page, settings.JiraUrl);
        var begin = Stopwatch.GetTimestamp();
        await Ui.ClickAsync("issue.assignToMe", ["Assign to me", "Atribuir a mim"]);
        mutations.MarkActivity();
        diagnostics?.Event("assign.wait_started", "Aguardando o término do envio da atribuição.");
        // A mudança visual é otimista: navegar antes da resposta pode cancelar a gravação.
        do
        {
            if (mutations.Settled)
            {
                var assigned = await AssigneeAsync();
                var link = Page.GetByText(new Regex(@"^(Assign to me|Atribuir a mim)$", RegexOptions.IgnoreCase)).Filter(new() { Visible = true });
                var matches = string.IsNullOrWhiteSpace(run.TechnicianName)
                    ? assigned.Length > 0 && !Regex.IsMatch(assigned, @"^(Unassigned|Não atribuído|None)$", RegexOptions.IgnoreCase) && await link.CountAsync() == 0
                    : IsTechnician(assigned, run.TechnicianName);
                if (matches && mutations.Settled)
                {
                    if (string.IsNullOrWhiteSpace(run.TechnicianName)) run.TechnicianName = assigned;
                    diagnostics?.Event("assign.wait_finished", "Envio encerrado; a atribuição será conferida na página recarregada.",
                        details: new() { ["mutationCount"] = mutations.Count }, durationMs: Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
                    return;
                }
            }
            await Task.Delay(200);
        } while (Stopwatch.GetElapsedTime(begin) < TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var error = new InvalidOperationException($"O envio da atribuição de {floor.IssueKey} não foi confirmado. O progresso foi salvo; confira o responsável no Jira e retome a visita.");
        error.Data["DiagnosticCode"] = "ASSIGN_SUBMISSION_UNCONFIRMED";
        throw error;
    }

    private async Task<string> AssigneeAsync() => DomControls.Normalize(Regex.Replace(
        await Ui.ValueAsync("issue.assignee", ["Assignee", "Responsável"]),
        @"\s*\b(Assign to me|Atribuir a mim)\s*$", "", RegexOptions.IgnoreCase));

    private static bool IsTechnician(string assignee, string technician) =>
        !string.IsNullOrWhiteSpace(technician) && string.Equals(DomControls.Normalize(assignee), DomControls.Normalize(technician), StringComparison.OrdinalIgnoreCase);

    private async Task OpenTransitionAsync(string key, string action, CancellationToken cancellationToken = default)
    {
        action = DomControls.Normalize(action);
        var direct = Page.GetByText(action, new() { Exact = true }).Filter(new() { Visible = true });
        cancellationToken.ThrowIfCancellationRequested();
        if (!await DomControls.IsUniqueVisibleAsync(direct))
            await (await StatusButtonAsync(cancellationToken)).ClickAsync();
        await Ui.ClickAsync(key, [action], waitTimeout: TimeSpan.FromSeconds(settings.TimeoutSeconds), cancellationToken: cancellationToken);
    }

    public async Task StartAsync(VisitRun run, FloorRun floor)
    {
        var status = await StatusAsync();
        if (!SameText(status, Workflow.WaitingStatus))
            throw new InvalidOperationException($"{floor.IssueKey} está em “{status}”. O início de atendimento esperado é a partir de {DomControls.Normalize(Workflow.WaitingStatus)}.");
        var startTransition = DomControls.Normalize(Workflow.StartTransition);
        using var mutations = new MutationTracker(Page, settings.JiraUrl);
        await OpenTransitionAsync("issue.start", startTransition);
        var begin = Stopwatch.GetTimestamp();
        mutations.MarkActivity();
        var confirmationClicked = false;
        diagnostics?.Event("start.wait_started", "Aguardando o resultado do início do atendimento.");
        do
        {
            var button = await StatusButtonLocatorAsync();
            var started = await DomControls.IsUniqueVisibleAsync(button) && SameText(await button.InnerTextAsync(), Workflow.InProgressStatus);
            // O Jira pode aplicar a transicao diretamente e manter outros dialogos visiveis.
            // Nao procurar uma segunda confirmacao se o status ja mudou, nem navegar
            // enquanto a interface antecipa um resultado que ainda esta sendo salvo.
            if (started)
            {
                if (mutations.Settled && await button.IsEnabledAsync())
                {
                    if (!mutations.Settled) continue;
                    diagnostics?.Event("start.wait_finished", "Envio encerrado; o atendimento será conferido na página recarregada.",
                        durationMs: Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
                    return;
                }
            }
            else if (!confirmationClicked)
            {
                // O painel do chamado tambem pode ter role=dialog. A confirmacao
                // precisa pertencer ao inicio do atendimento e ter um botao real.
                var dialogs = Page.GetByRole(AriaRole.Dialog).Filter(new() { Visible = true, HasNot = button });
                ILocator confirm;
                if (settings.SelectorOverrides.TryGetValue("start.confirm", out var selector))
                    confirm = dialogs.Locator(selector).Filter(new() { Visible = true });
                else
                    confirm = dialogs.Filter(new() { Has = Page.GetByText(startTransition, new() { Exact = true }) })
                        .GetByRole(AriaRole.Button, new() { NameRegex = new($"^({Regex.Escape(startTransition)}|Confirm|Confirmar)$", RegexOptions.IgnoreCase) })
                        .Filter(new() { Visible = true });
                var count = await confirm.CountAsync();
                if (count > 1)
                {
                    diagnostics?.Event("control.ambiguous", "Mais de uma confirmação de início foi encontrada.", DiagnosticLevel.Warning,
                        new() { ["controlKey"] = "start.confirm", ["count"] = count }, errorCode: "FIELD_AMBIGUOUS");
                    var error = new InvalidOperationException("O Jira exibiu mais de uma confirmação de início do atendimento. Confira a página antes de retomar.");
                    error.Data["DiagnosticCode"] = "FIELD_AMBIGUOUS";
                    throw error;
                }
                if (count == 1 && await confirm.IsEnabledAsync())
                {
                    await confirm.ClickAsync(new() { Timeout = (float)Math.Max(1,
                        (TimeSpan.FromSeconds(settings.TimeoutSeconds) - Stopwatch.GetElapsedTime(begin)).TotalMilliseconds) });
                    confirmationClicked = true;
                    mutations.MarkActivity();
                    diagnostics?.Event("action.clicked", "Confirmação do início enviada; o resultado ainda requer conferência.",
                        details: new() { ["controlKey"] = "start.confirm" }, outcome: "clicked");
                }
            }
            await Task.Delay(200);
        } while (Stopwatch.GetElapsedTime(begin) < TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var timeout = new InvalidOperationException($"O início do atendimento de {floor.IssueKey} não foi confirmado no tempo esperado. O progresso foi salvo; confira o Jira e retome a visita.");
        timeout.Data["DiagnosticCode"] = "START_SUBMISSION_UNCONFIRMED";
        throw timeout;
    }

    public async Task PrepareCloseAsync(VisitRun run, FloorRun floor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SameText(await StatusAsync(cancellationToken), Workflow.InProgressStatus))
            throw new InvalidOperationException($"{floor.IssueKey}: confira o início do atendimento antes do fechamento.");
        var closeTransition = DomControls.Normalize(Workflow.CloseTransition);
        await OpenTransitionAsync("issue.close", closeTransition, cancellationToken);
        // Outros diálogos podem estar abertos antes do formulário carregar: o aviso de cookies
        // de um perfil novo do Edge ou o próprio painel do chamado. O formulário de fechamento
        // é o diálogo com o botão da transição e sem o controle de status.
        var submit = settings.SelectorOverrides.TryGetValue("close.submit", out var submitSelector)
            ? Page.Locator(submitSelector) : Page.GetByRole(AriaRole.Button, new() { Name = closeTransition, Exact = true });
        var dialog = Page.GetByRole(AriaRole.Dialog).Filter(new() { Visible = true, HasNot = await StatusButtonLocatorAsync() })
            .Filter(new() { Has = submit });
        await dialog.First.WaitForAsync();
        dialog = await DomControls.UniqueAsync(dialog, "Fechamento");
        using var mutations = new MutationTracker(Page, settings.JiraUrl);
        var resolution = await Ui.FillAsync("close.resolution", Labels("resolution"), floor.Floor.Resolution, dialog);
        // Seleciona explicitamente o comentário público antes de preencher o editor.
        var reply = dialog.GetByText("Reply to customer", new() { Exact = true });
        if (await DomControls.IsUniqueVisibleAsync(reply)) await reply.ClickAsync();
        else await Ui.ClickAsync("close.publicMode", ["Reply to customer", "Responder ao cliente"], dialog);
        var comment = await Ui.FillAsync("close.publicComment", ["Comentário público", "Reply to customer", "Responder ao cliente", "Comentário", "Comment"], floor.Floor.Resolution, dialog);
        // Espaços extras digitados em Configurações não fazem parte do nome da equipe.
        var team = DomControls.Normalize(run.Settings.ClosingTeam);
        await Ui.ChooseAsync("close.team", Labels("team"), team, new Regex($"^{Regex.Escape(team)}$"), team, dialog,
            "Corrija a Equipe de fechamento em Configurações e retome a visita.");
        await WaitReadyToSubmitAsync("close.submit", [closeTransition], dialog, mutations,
            [(resolution, floor.Floor.Resolution), (comment, floor.Floor.Resolution)], cancellationToken);
        closingDialog = dialog;
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task SubmitCloseAsync(VisitRun run, FloorRun floor)
    {
        var dialog = closingDialog ?? throw new InvalidOperationException("O formulário de fechamento ainda não foi preparado.");
        try
        {
            using var mutations = new MutationTracker(Page, settings.JiraUrl);
            var begin = Stopwatch.GetTimestamp();
            await Ui.ClickAsync("close.submit", [DomControls.Normalize(Workflow.CloseTransition)], dialog);
            mutations.MarkActivity();
            diagnostics?.Event("close.wait_started", "Aguardando o término do envio do fechamento.");
            // O Jira pode exibir Fechado antes de salvar. Não navegar enquanto o modal
            // ou as requisições de gravação estiverem ativos, mesmo com esse status.
            do
            {
                if (mutations.Settled && !await dialog.IsVisibleAsync())
                {
                    var statusButton = await StatusButtonLocatorAsync();
                    if (await DomControls.IsUniqueVisibleAsync(statusButton) && await statusButton.IsEnabledAsync() &&
                        IsClosedStatus(await statusButton.InnerTextAsync()))
                    {
                        if (!mutations.Settled) continue;
                        diagnostics?.Event("close.wait_finished", "Envio encerrado; o fechamento será conferido na página recarregada.",
                            details: new() { ["mutationCount"] = mutations.Count }, durationMs: Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
                        return;
                    }
                }
                await Task.Delay(200);
            } while (Stopwatch.GetElapsedTime(begin) < TimeSpan.FromSeconds(settings.TimeoutSeconds));
            var error = new ReconciliationException($"O envio do fechamento de {floor.IssueKey} não terminou no tempo esperado. O progresso foi salvo; confira o Jira antes de tentar novamente.");
            error.Data["DiagnosticCode"] = "CLOSE_SUBMISSION_UNCONFIRMED";
            throw error;
        }
        finally { closingDialog = null; }
    }

    // Retorna null quando a página não oferece onde conferir a resolução.
    private async Task<bool?> ResolutionMatchesAsync(VisitRun run, string expected)
    {
        var directValue = await Ui.TryValueAsync("issue.resolution", Labels("resolution"));
        if (directValue is not null)
        { diagnostics?.Event("verification.source", "Origem da conferência da resolução.", DiagnosticLevel.Debug, new() { ["source"] = "issue_field" }); return DomControls.Normalize(directValue) == DomControls.Normalize(expected); }
        // Esse campo aparece no modal, mas não está no layout principal do Jira da unidade.
        // O histórico registra separadamente os valores anteriores e os novos valores salvos.
        // A seção de atividade carrega depois dos campos, e o Jira mostra o histórico como aba, como botão
        // ou como opção do menu "Mostrar:", conforme a largura disponível.
        // O Jira também remonta a atividade e volta a página para o topo enquanto carrega. Por isso a abertura é
        // repetida até o histórico aparecer, e cada rodada rola, procura e clica numa única chamada à página.
        var updates = Page.Locator("[data-testid='issue-history.ui.history-items.generic-history-item.history-item']")
            .Filter(new() { Has = Page.GetByText(DomControls.LabelPattern(Labels("resolution")[0])), Visible = true });
        var begin = Stopwatch.GetTimestamp();
        var limit = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        var lastClick = 0L;
        var historyFound = false;
        var lastState = "";
        string? savedValue = null;
        while (true)
        {
            if (await updates.CountAsync() > 0)
            {
                try { savedValue = await updates.First.EvaluateAsync<string>("e => e.lastElementChild?.lastElementChild?.lastElementChild?.innerText || ''", null, new() { Timeout = 2000 }); break; }
                catch (Exception ex) when (ex is TimeoutException or PlaywrightException) { }
            }
            if (Stopwatch.GetElapsedTime(begin) >= limit) break;
            // Depois de um clique, o histórico tem alguns segundos para carregar antes de um novo clique.
            var mayClick = lastClick == 0 || Stopwatch.GetElapsedTime(lastClick) > TimeSpan.FromSeconds(3);
            try { lastState = await Page.EvaluateAsync<string>(OpenHistoryScript, mayClick); }
            catch (PlaywrightException) { lastState = "navigating"; }
            if (lastState == "clicked") lastClick = Stopwatch.GetTimestamp();
            historyFound |= lastState is "clicked" or "selected";
            await Task.Delay(500);
        }
        if (savedValue is null && !historyFound)
        {
            diagnostics?.Event("verification.unavailable", "Histórico do chamado não encontrado para conferir a resolução.", DiagnosticLevel.Warning,
                new() { ["controlKey"] = "issue.history", ["state"] = lastState }, durationMs: Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
            await CaptureMomentAsync(run, "historico-ausente", "Captura do chamado sem Histórico disponível.");
            return null;
        }
        diagnostics?.Event("verification.source", "Origem da conferência da resolução.", DiagnosticLevel.Debug, new() { ["source"] = "issue_history" });
        if (savedValue is null)
        {
            diagnostics?.Event("verification.unavailable", "Nenhuma alteração da resolução aparece no histórico.", DiagnosticLevel.Warning,
                new() { ["controlKey"] = "issue.history" });
            await CaptureMomentAsync(run, "historico-sem-resolucao", "Captura do Histórico sem a resolução disponível.");
            return false;
        }
        // O histórico mostra a atualização mais recente primeiro. Nunca aceitar um valor antigo.
        var matches = DomControls.Normalize(savedValue) == DomControls.Normalize(expected);
        if (!matches) await CaptureMomentAsync(run, "resolucao-divergente", "Captura da resolução divergente disponível.");
        return matches;
    }

    // Rola cada área até o fim, porque o Jira só monta a atividade quando ela aparece na tela, e abre o histórico.
    // O clique vai direto no elemento: o Jira remonta os controles antes de ficarem estáveis para o clique comum.
    // Gatilhos de menu ficam de fora, porque o "Mostrar:" exibe o nome da opção escolhida.
    // Resultados: selected, clicked, menu (abriu o "Mostrar:"), ambiguous, waiting (aguardando o clique) ou missing.
    private const string OpenHistoryScript = """
        mayClick => {
          for (const e of [document.scrollingElement, ...document.querySelectorAll('*')])
            if (e && e.scrollHeight > e.clientHeight + 1 && /auto|scroll/.test(getComputedStyle(e).overflowY)) e.scrollTop = e.scrollHeight;
          window.scrollTo(0, document.body.scrollHeight);
          const visible = e => e.getClientRects().length > 0 && getComputedStyle(e).visibility !== 'hidden';
          const name = e => (e.getAttribute('aria-label') || e.innerText || '').trim();
          const controls = [...document.querySelectorAll('button,[role=tab],[role=button],[role=radio],[role=menuitemradio],[role=menuitem],[role=option]')]
            .filter(e => visible(e) && /^(History|Histórico)$/i.test(name(e)) && !e.hasAttribute('aria-haspopup') && !e.hasAttribute('aria-expanded'));
          if (controls.length > 1) return 'ambiguous';
          if (controls.length === 1) {
            const control = controls[0];
            if (control.getAttribute('aria-selected') === 'true' || control.getAttribute('aria-checked') === 'true') return 'selected';
            if (!mayClick) return 'waiting';
            control.click();
            return 'clicked';
          }
          const label = [...document.querySelectorAll('body *')].find(e => e.children.length === 0 && visible(e) && /^(Mostrar|Show):?$/i.test(e.innerText.trim()));
          const menu = label && document.evaluate('following::button[1]', label, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null).singleNodeValue;
          if (!menu || !visible(menu) || menu.getAttribute('aria-expanded') === 'true') return 'missing';
          if (!mayClick) return 'waiting';
          menu.click();
          return 'menu';
        }
        """;

    // Sem o Histórico na página, vale o fechamento pendente desta visita: a pendência só é salva depois que o formulário
    // fica pronto com esta resolução conferida, e o Jira grava os campos da transição junto com o novo status.
    // O status fechado, o comentário público e a equipe continuam conferidos na página.
    // Um pavimento que esta visita já marcou como fechado teve a resolução conferida antes; a retomada não a recusa.
    private bool ResolutionVerifiedByVisit(FloorRun floor)
    {
        if (floor.Stage == FloorStage.Closed)
        {
            diagnostics?.Event("verification.source", "Origem da conferência da resolução.", DiagnosticLevel.Debug, new() { ["source"] = "close_verified" });
            return true;
        }
        if (floor.PendingAction != PendingAction.Close) return false;
        diagnostics?.Event("verification.source", "Origem da conferência da resolução.", DiagnosticLevel.Debug, new() { ["source"] = "transition_form" });
        return true;
    }

    private async Task<bool> PublicCommentMatchesAsync(VisitRun run, string key, string expected, CancellationToken cancellationToken)
    {
        var matches = await FindPublicCommentAsync(run, key, expected, cancellationToken);
        if (!matches) await CaptureMomentAsync(run, "comentario-ausente", "Captura do comentário público não encontrado disponível.");
        return matches;
    }

    private async Task<bool> FindPublicCommentAsync(VisitRun run, string key, string expected, CancellationToken cancellationToken)
    {
        foreach (var name in new[] { "Comments", "Comentários" })
        {
            var tab = Page.GetByRole(AriaRole.Tab, new() { Name = name, Exact = true });
            if (await DomControls.IsUniqueVisibleAsync(tab)) { await tab.ClickAsync(); break; }
        }
        if (settings.SelectorOverrides.TryGetValue("issue.publicComment", out var selector))
        {
            diagnostics?.Event("verification.source", "Origem da conferência do comentário público.", DiagnosticLevel.Debug, new() { ["source"] = "public_comment_override" });
            var comments = Page.Locator(selector);
            for (var i = 0; i < await comments.CountAsync(); i++)
                if (DomControls.Normalize(await comments.Nth(i).InnerTextAsync()) == DomControls.Normalize(expected)) return true;
            return false;
        }
        var portalLink = Page.GetByRole(AriaRole.Link, new() { NameRegex = new(@"^(View request in portal|Ver solicitação no portal)$", RegexOptions.IgnoreCase) }).Filter(new() { Visible = true });
        if (await DomControls.IsUniqueVisibleAsync(portalLink))
            return await PortalCommentMatchesAsync(run, key, expected, (await portalLink.GetAttributeAsync("href"))!, cancellationToken);
        var markers = Page.GetByText(new Regex(@"^(Comentário público|Public comment|Replied to customer|Reply to customer|Compartilhado com o cliente)$", RegexOptions.IgnoreCase));
        for (var i = 0; i < await markers.CountAsync(); i++)
        {
            var node = markers.Nth(i);
            var contentsInComment = await node.EvaluateAsync<string[]>("e => Array.from(e.closest('article,[role=article],[data-testid*=comment]')?.querySelectorAll('pre,p,[data-testid*=body]') || []).map(n => n.innerText || '')");
            if (contentsInComment.Any(text => DomControls.Normalize(text) == DomControls.Normalize(expected))) return true;
            // A leitura fica restrita ao comentário; o corpo do chamado também contém a resolução.
            for (var depth = 0; depth < 2; depth++)
            {
                node = node.Locator("..");
                var tag = await node.EvaluateAsync<string>("e => e.tagName.toLowerCase()");
                if (tag is "body" or "main" or "section") break;
                var contents = node.Locator("pre,p,[data-testid*=body]");
                for (var j = 0; j < await contents.CountAsync(); j++)
                    if (DomControls.Normalize(await contents.Nth(j).InnerTextAsync()) == DomControls.Normalize(expected)) return true;
            }
        }
        return false;
    }

    private async Task<bool> PortalCommentMatchesAsync(VisitRun run, string key, string expected, string href, CancellationToken cancellationToken)
    {
        var originalUrl = Page.Url;
        var target = new Uri(new Uri(originalUrl), href);
        if (target.Authority != new Uri(settings.JiraUrl).Authority ||
            !Regex.IsMatch(target.AbsolutePath, $"^/servicedesk/customer/portal/\\d+/{Regex.Escape(key)}/?$"))
            throw new InvalidOperationException("O link do portal não corresponde ao chamado em conferência.");
        try
        {
            diagnostics?.Event("verification.source", "Origem da conferência do comentário público.", DiagnosticLevel.Debug, new() { ["source"] = "customer_portal" });
            await NavigateAsync(target.AbsoluteUri);
            await Page.GetByText(key, new() { Exact = true }).First.WaitForAsync(new() { State = WaitForSelectorState.Visible });
            // No portal do cliente, ler apenas comentários publicados; descrições e editores ficam excluídos.
            var bodies = Page.Locator(".ak-renderer-wrapper.is-comment .ak-renderer-document").Filter(new() { Visible = true });
            var deadline = DateTimeOffset.UtcNow.AddSeconds(settings.TimeoutSeconds);
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var body in await bodies.AllInnerTextsAsync())
                    if (DomControls.Normalize(body) == DomControls.Normalize(expected)) return true;
                await Task.Delay(300, cancellationToken);
            } while (DateTimeOffset.UtcNow < deadline);
            // Captura o portal antes de voltar ao chamado.
            await CaptureMomentAsync(run, "comentario-ausente", "Captura do comentário público não encontrado disponível.");
            return false;
        }
        finally { if (!Page.IsClosed) await NavigateAsync(originalUrl); }
    }

    public async Task CaptureFailureAsync(VisitRun run)
    {
        // A falha pode chegar com a página recarregando; sem esperar, a captura mostraria só o esqueleto do Jira.
        if (page is not null && !page.IsClosed)
        {
            try
            {
                var begin = Stopwatch.GetTimestamp();
                if (PageKind(page.Url) != "issue") await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5000 });
                else while (!await DomControls.IsUniqueVisibleAsync(await StatusButtonLocatorAsync()) && Stopwatch.GetElapsedTime(begin) < TimeSpan.FromSeconds(10))
                    await Task.Delay(300);
            }
            catch (Exception ex) when (ex is TimeoutException or PlaywrightException) { }
        }
        await CaptureAsync(run, "falha", "Captura de falha disponível.");
    }

    // Captura a página no momento em que a conferência encontra o problema, antes de navegar para outra página.
    // A captura nunca altera o resultado da conferência. Sem uma tentativa registrada, ela não entraria em nenhum pacote.
    private async Task CaptureMomentAsync(VisitRun run, string label, string message)
    {
        if (diagnostics is null || !capturedMoments.Add(label)) return;
        try { await CaptureAsync(run, label, message); }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException or IOException or UnauthorizedAccessException)
        { diagnostics?.Event("artifact.skipped", "Não foi possível capturar a página neste momento.", DiagnosticLevel.Warning); }
    }

    private async Task CaptureAsync(VisitRun run, string label, string message)
    {
        if (page is null || page.IsClosed)
        { diagnostics?.Event("artifact.skipped", "Captura indisponível: navegador fechado ou não conectado.", DiagnosticLevel.Warning); return; }
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var uri) || uri.Authority != new Uri(settings.JiraUrl).Authority ||
            PageKind(page.Url) == "authentication" || await page.Locator("input[type=password]").CountAsync() > 0)
        { diagnostics?.Event("artifact.skipped", "Captura não permitida nesta página.", DiagnosticLevel.Warning); return; }
        var attemptId = diagnostics?.Id ?? Guid.NewGuid().ToString("N");
        var directory = store.Diagnostics?.ArtifactDirectory(run.Id, attemptId) ?? Path.Combine(store.DiagnosticsDirectory(run.Id), "attempts", attemptId, "artifacts");
        Directory.CreateDirectory(directory);
        var filename = $"{Guid.NewGuid():N}-{label}.png";
        var profile = run.Profile;
        string[] privateValues = [profile.ReporterName, profile.ReporterEmail, profile.FullName, profile.Extension, profile.Phone,
            profile.Unit, profile.Description, run.TechnicianName, .. run.Floors.Select(f => f.Floor.Resolution)];
        var masks = new List<ILocator> { page.Locator("input,textarea,[contenteditable=true]") };
        masks.AddRange(privateValues.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().Select(v => page.GetByText(v, new() { Exact = false })));
        await page.ScreenshotAsync(new() { Path = Path.Combine(directory, filename), FullPage = true, Mask = masks, Timeout = 10000 });
        diagnostics?.Event("artifact.saved", message, artifacts: ["artifacts/" + filename]);
    }

    public async ValueTask DisposeAsync()
    {
        try { if (context is not null) await context.CloseAsync(); }
        finally { playwright?.Dispose(); }
    }
}
