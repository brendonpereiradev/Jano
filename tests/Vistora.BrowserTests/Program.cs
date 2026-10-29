using System.Net;
using System.Text.Json;
using System.Net.Sockets;
using Microsoft.Playwright;
using Vistora.Automation.Edge;
using Vistora.Core;
using Vistora.Infrastructure;

if (args.Contains("--validate-saved-issues"))
{
    var savedStore = new LocalStore();
    var savedRuns = await savedStore.LoadRunsAsync();
    var savedRun = VisitRunSelection.ForResume(savedRuns, savedRuns.First());
    Console.WriteLine($"Retomada direcionada para {savedRun.DateLabel}, com {savedRun.Floors.Count(f => f.IssueKey is not null)} chamados registrados.");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    await using var automation = new EdgeJiraAutomation(savedStore);
    await automation.ConnectAsync(savedRun.Settings, Console.WriteLine, cancellation.Token, savedRun.Floors.First(f => f.IssueKey is not null).IssueKey);
    foreach (var floor in savedRun.Floors.Where(f => f.IssueKey is not null))
    {
        var snapshot = await automation.ReadIssueAsync(savedRun, floor, cancellation.Token);
        Check(!snapshot.Closed || (snapshot.ResolutionMatches && snapshot.PublicCommentMatches && snapshot.TeamMatches), $"{snapshot.Key}: resolução, comentário público ou equipe não conferidos.");
        Console.WriteLine($"PASS: {snapshot.Key} — dados conferidos, status {snapshot.Status}.");
    }
    Console.WriteLine("Apenas leitura; chamados e histórico preservados.");
    return 0;
}

if (args.Contains("--diagnose-saved-issue"))
{
    var savedStore = new LocalStore();
    var savedRun = (await savedStore.LoadRunsAsync()).First(r => r.Floors.Any(f => f.IssueKey is not null));
    var key = savedRun.Floors.First(f => f.IssueKey is not null).IssueKey!;
    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchPersistentContextAsync(savedStore.BrowserDirectory,
        new() { Channel = "msedge", Headless = false, ChromiumSandbox = true });
    var page = browser.Pages[0];
    await page.GotoAsync(savedRun.Settings.JiraUrl.TrimEnd('/') + savedRun.Settings.Jira.IssuePath.Trim().Replace("{project}", key.Split('-')[0]).Replace("{key}", key), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
    await page.GetByRole(AriaRole.Heading, new() { Name = savedRun.Profile.Title, Exact = true }).WaitForAsync();
    Console.WriteLine("Controles com texto de status: " + await page.Locator("button,[role=button]").EvaluateAllAsync<string>("(els,names)=>JSON.stringify(els.filter(e=>names.some(n=>(e.innerText||'').toLowerCase().includes(n.toLowerCase()))).map(e=>({text:e.innerText,html:e.outerHTML})))",
        new[] { savedRun.Settings.Jira.WaitingStatus, savedRun.Settings.Jira.InProgressStatus }.Concat(savedRun.Settings.Jira.ClosedStatuses).ToArray()));
    await Task.Delay(1000);
    Console.WriteLine("Controles de leitura: " + await page.Locator("button,[role=button],[role=tab],h2,h3,label").EvaluateAllAsync<string>("els=>JSON.stringify(els.filter(e=>/Resolu|Equipe|Comment|Activity|Details|Show|More|Mostrar|Exibir|Expand|Recolher|View/i.test(e.innerText||e.getAttribute('aria-label')||'')).map(e=>({text:e.innerText.slice(0,200),label:e.getAttribute('aria-label'),testid:e.getAttribute('data-testid')})))"));
    var resolutionLabels = page.GetByText(new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(JiraFields.Labels(savedRun.Settings, "resolution")[0]), System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    Console.WriteLine("Rótulos de resolução: " + await resolutionLabels.EvaluateAllAsync<string>("els=>JSON.stringify(els.map(e=>({text:e.innerText,html:e.outerHTML,parent:e.parentElement.outerHTML.slice(0,2400)})))"));
    Console.WriteLine("Trechos de fechamento: " + await page.Locator("body").EvaluateAsync<string>("e=>JSON.stringify(e.innerText.split('\\n').map((line,i,lines)=>/Resolu|Equipe|Comment|Activity|Details|Show|More|Mostrar|Exibir|Expand|Recolher|View/i.test(line)?lines.slice(Math.max(0,i-1),i+4).join(' | '):'').filter(Boolean))"));
    foreach (var tabName in new[] { "Comments", "Comentários", "History", "Histórico" })
    {
        var tab = page.GetByRole(AriaRole.Tab, new() { Name = tabName, Exact = true }).Or(page.GetByRole(AriaRole.Button, new() { Name = tabName, Exact = true }));
        Console.WriteLine($"Aba {tabName}: {await tab.CountAsync()}");
        if (await tab.CountAsync() != 1) continue;
        await tab.ClickAsync(); await Task.Delay(1500);
        Console.WriteLine($"Conteúdo {tabName}: " + await page.GetByRole(AriaRole.Tabpanel).EvaluateAllAsync<string>("els=>JSON.stringify(els.map(e=>({text:e.innerText,html:(()=>{const clone=e.cloneNode(true);for(const node of clone.querySelectorAll('*')){node.removeAttribute('class');node.removeAttribute('style')}return clone.outerHTML.slice(0,18000)})()})))"));
    }
    var portalLink = page.GetByRole(AriaRole.Link, new() { Name = "View request in portal", Exact = true });
    Console.WriteLine("Link do portal: " + await portalLink.GetAttributeAsync("href"));
    await page.GotoAsync(new Uri(new Uri(page.Url), (await portalLink.GetAttributeAsync("href"))!).AbsoluteUri); await Task.Delay(1500);
    Console.WriteLine("Portal atual: " + new Uri(page.Url).GetLeftPart(UriPartial.Path));
    var commentOpening = savedRun.Floors[0].Floor.Resolution.Split('\n')[0].Trim();
    Console.WriteLine("Comentário no portal: " + await page.GetByText(commentOpening, new() { Exact = true }).EvaluateAllAsync<string>("els=>JSON.stringify(els.map(e=>{let node=e;const parents=[];for(let i=0;i<5&&node;i++,node=node.parentElement){const clone=node.cloneNode(true);for(const child of clone.querySelectorAll('*')){child.removeAttribute('class');child.removeAttribute('style')}parents.push(clone.outerHTML.slice(0,7000))}return parents}))"));
    Console.WriteLine("Controles do portal: " + await page.Locator("button,[role=button],article,[data-testid*=comment],[data-test-id*=comment]").EvaluateAllAsync<string>("els=>JSON.stringify(els.map(e=>({tag:e.tagName,id:e.id,class:e.className,testid:e.getAttribute('data-testid'),testId:e.getAttribute('data-test-id'),text:e.innerText?.slice(0,500)})))"));
    Console.WriteLine("Apenas leitura; nenhuma atribuição ou transição executada.");
    return 0;
}

if (args.Contains("--validate-saved-portal"))
{
    var savedStore = new LocalStore();
    var savedSettings = await savedStore.LoadSettingsAsync();
    var savedProfiles = await savedStore.LoadProfilesAsync();
    var activeProfile = Serialization.Copy(savedProfiles.Profiles.Single(p => p.Id == savedProfiles.ActiveProfileId));
    activeProfile.Attachments.Clear(); // A conferência não envia anexos nem o formulário.
    var validationRun = ProfileValidation.CreateRun(activeProfile, savedSettings);
    using var validationCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    await using var automation = new EdgeJiraAutomation(savedStore);
    await automation.ConnectAsync(savedSettings, Console.WriteLine, validationCancellation.Token);
    await automation.PrepareCreateAsync(validationRun, validationRun.Floors[0], validationCancellation.Token);
    Console.WriteLine("PASS: formulário real reconhecido e campos preenchidos/conferidos. Nenhum chamado enviado.");
    return 0;
}

if (args.Contains("--probe-jira") || args.Contains("--diagnose-portal"))
{
    using var playwright = await Playwright.CreateAsync();
    var savedStore = new LocalStore();
    var savedSettings = await savedStore.LoadSettingsAsync();
    var probeRoot = args.Contains("--saved-session") ? savedStore.BrowserDirectory : Path.Combine(Path.GetTempPath(), "VistoraProbe", Guid.NewGuid().ToString("N"));
    await using var browser = await playwright.Chromium.LaunchPersistentContextAsync(probeRoot, new() { Channel = "msedge", Headless = false, ChromiumSandbox = true });
    var page = browser.Pages[0];
    await page.GotoAsync(savedSettings.PortalUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
    await Task.Delay(1500);
    Console.WriteLine("Página: " + new Uri(page.Url).GetLeftPart(UriPartial.Path));
    Console.WriteLine("Título: " + await page.TitleAsync());
    if (args.Contains("--diagnose-portal"))
    {
        var marker = page.GetByText(new System.Text.RegularExpressions.Regex($@"^{System.Text.RegularExpressions.Regex.Escape(JiraFields.Labels(savedSettings, "reporter")[0])}\s*\*?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Console.WriteLine("Marcadores encontrados pela versão anterior: " + await marker.CountAsync());
        for (var i = 0; i < await marker.CountAsync(); i++)
            Console.WriteLine("Marcador: " + await marker.Nth(i).EvaluateAsync<string>("e => JSON.stringify({tag:e.tagName,text:e.textContent,visible:!!(e.offsetWidth||e.offsetHeight||e.getClientRects().length)})"));
        Console.WriteLine("Rótulos: " + await page.Locator("label").EvaluateAllAsync<string>("els => JSON.stringify(els.map(e=>({text:e.textContent,for:e.htmlFor})))"));
        Console.WriteLine("Campos: " + await page.Locator("input,select,textarea,[contenteditable=true]").EvaluateAllAsync<string>("els => JSON.stringify(els.map(e=>({tag:e.tagName,id:e.id,role:e.getAttribute('role'),label:e.getAttribute('aria-label'),labelledby:e.getAttribute('aria-labelledby'),type:e.getAttribute('type')})))"));
        if (args.Contains("--saved-session"))
        {
            var document = await savedStore.LoadProfilesAsync();
            var reporterEmail = document.Profiles.Single(p => p.Id == document.ActiveProfileId).ReporterEmail;
            var reporterField = page.Locator("#reporter");
            await reporterField.FocusAsync(); await reporterField.FillAsync(reporterEmail); await Task.Delay(2000);
            Console.WriteLine("Opções do solicitante: " + await page.GetByRole(AriaRole.Option).EvaluateAllAsync<string>("els=>JSON.stringify(els.map(e=>({text:e.innerText,html:e.outerHTML})))"));
            Console.WriteLine("Listas: " + await page.GetByRole(AriaRole.Listbox).EvaluateAllAsync<string>("els=>JSON.stringify(els.map(e=>({text:e.innerText,html:e.outerHTML})))"));
            var emailPattern = new System.Text.RegularExpressions.Regex($"(?:^|[\\s(<]){System.Text.RegularExpressions.Regex.Escape(reporterEmail)}(?=$|[\\s)>])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            await page.GetByRole(AriaRole.Option, new() { NameRegex = emailPattern }).ClickAsync();
            await Task.Delay(400);
            Console.WriteLine("Seleção: " + await reporterField.EvaluateAsync<string>("e=>JSON.stringify({value:e.value,describedby:e.getAttribute('aria-describedby'),refs:(e.getAttribute('aria-describedby')||'').split(/\\s+/).map(id=>({id,text:document.getElementById(id)?.textContent})),container:e.parentElement.parentElement.outerHTML})"));
        }
    }
    Console.WriteLine("Sondagem do formulário. Nenhum chamado criado.");
    return 0;
}

var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var app = builder.Build();
var hidePublic = false;
var wrongLatestResolution = false;
var loginMode = false;
var incompleteForm = false;
var passwordForm = false;
var portalVisits = 0;
var rejectClosing = false;
var closeRequests = 0;
var transitionDelayMs = 1200;
var missingTransition = false;
var ambiguousTransition = false;
var assignmentDelayMs = 1500;
var rejectAssignment = false;
var assignmentRequests = 0;
var startRequests = 0;
var startDelayMs = 1500;
var startMode = "unrelated";
var rejectStart = false;
var disabledSubmit = false;
var hideHistory = false;
AppSettings ExampleSettings() => Fixture.ExampleSettings();
JiraWorkflow ExampleWorkflow() => ExampleSettings().Jira;
var workflow = ExampleWorkflow();
app.MapPost("/fixture/start", async (HttpContext ctx) =>
{
    startRequests++;
    await Task.Delay(startDelayMs, ctx.RequestAborted);
    return rejectStart ? Results.BadRequest() : Results.Ok();
});
app.MapPost("/fixture/assign", async (HttpContext ctx) =>
{
    assignmentRequests++;
    await Task.Delay(assignmentDelayMs, ctx.RequestAborted);
    return rejectAssignment ? Results.BadRequest() : Results.Ok();
});
app.MapPost("/fixture/close", async (HttpContext ctx) =>
{
    closeRequests++;
    await Task.Delay(1500, ctx.RequestAborted);
    return rejectClosing ? Results.BadRequest() : Results.Ok();
});
app.MapGet("/{**path}", (HttpContext ctx) =>
{
    if (ctx.Request.Path.Value!.EndsWith("/create/1", StringComparison.Ordinal)) portalVisits++;
    if (loginMode && !ctx.Request.Path.Value!.EndsWith("/user/login", StringComparison.Ordinal))
        return Results.Redirect("/servicedesk/customer/portal/1/user/login");
    if (passwordForm) return Results.Content("<html><body><input type='password' value='senha-sentinel'></body></html>", "text/html; charset=utf-8");
    if (loginMode || incompleteForm)
        return Results.Content("<html><body><label>Solicitante*<span>(required)</span></label><p>Carregando</p></body></html>", "text/html; charset=utf-8");
    return Results.Content(Fixture.Html(hidePublic, wrongLatestResolution, transitionDelayMs, missingTransition, ambiguousTransition, startMode, disabledSubmit, hideHistory, workflow), "text/html; charset=utf-8");
});
await app.StartAsync();
var root = Path.Combine(Path.GetTempPath(), "VistoraBrowserTests", Guid.NewGuid().ToString("N"));
await using var diagnostics = new DiagnosticService(root);
diagnostics.Configure(new DiagnosticOptions { MinimumLevel = DiagnosticLevel.Debug });
var store = new LocalStore(root, diagnostics);
var profile = new VisitProfile
{
    Name = "Perfil de teste", ReporterName = "Pessóa Teste", ReporterEmail = "pessoa@example.com", FullName = "Pessoa Teste",
    Extension = "123456", Unit = "Unidade A", Floors = [new() { Name = "Subsolo", Sector = "DML", Room = "Sala A", Resolution = "Visita Subsolo\n\nComputador: OK\nImpressora: OK" },
    new() { Name = "Térreo", Sector = "Consultórios", Room = "Sala B", Resolution = "Visita Térreo\n\nAvayas: OK" }]
};
var run = ProfileValidation.CreateRun(profile, ExampleSettings());
run.Settings.JiraUrl = $"http://127.0.0.1:{port}";
run.Settings.PortalUrl = run.Settings.JiraUrl + "/servicedesk/customer/portal/1/group/1/create/1";
run.Settings.QueueUrl = run.Settings.JiraUrl + "/queue";
run.Settings.TimeoutSeconds = 10;
// Valor como pode ser digitado em Configurações; o Jira exibe "Equipe de Campo".
run.Settings.ClosingTeam = "  Equipe   de Campo ";
var engine = new ExecutionEngine(store, () => new EdgeJiraAutomation(store), diagnostics);
engine.Progress += r => Console.WriteLine(r.Message);
var failures = 0;
try
{
    using (var interruption = new CancellationTokenSource())
    {
        void StopAfterCreation(VisitRun progress)
        {
            if (progress.Floors.All(f => f.Stage == FloorStage.Created)) interruption.Cancel();
        }
        engine.Progress += StopAfterCreation;
        await engine.ExecuteAsync(run, interruption.Token);
        engine.Progress -= StopAfterCreation;
        Check(run.State == RunState.Interrupted && run.Floors.All(f => f.Stage == FloorStage.Created), "Não interrompeu antes da atribuição.");
    }
    var createdKeys = run.Floors.Select(f => f.IssueKey).ToArray();
    var portalVisitsBeforeResume = portalVisits;
    run = (await store.LoadRunsAsync()).Single();
    var resumeTime = System.Diagnostics.Stopwatch.StartNew();
    await engine.ExecuteAsync(run, default);
    Console.WriteLine($"TEMPO: atribuir, iniciar e fechar levou {resumeTime.Elapsed.TotalSeconds / run.Floors.Count:0.0} s por pavimento.");
    Check(run.State == RunState.Completed, run.Message);
    Check(assignmentRequests == 2, "A espera repetiu a atribuição.");
    Check(startRequests == 2, "O inicio do atendimento foi reenviado durante a espera.");
    startMode = "direct";
    startDelayMs = 0;
    assignmentDelayMs = 0;
    Check(closeRequests == 2, "O fechamento foi reenviado durante a espera.");
    Check(run.Floors.Select(f => f.IssueKey).SequenceEqual(createdKeys), "A retomada substituiu os chamados existentes.");
    Check(portalVisits == portalVisitsBeforeResume, "A retomada abriu o formulário de criação.");
    await diagnostics.FlushAsync();
    var eventFiles = Directory.EnumerateFiles(store.DiagnosticsDirectory(run.Id), "events.jsonl", SearchOption.AllDirectories).ToArray();
    var eventText = string.Concat(await Task.WhenAll(eventFiles.Select(path => File.ReadAllTextAsync(path))));
    Check(eventFiles.Length == 2 && eventText.Contains("close.confirmed") && eventText.Contains("control.lookup"), "Linha do tempo do navegador ausente.");
    var transitionEvents = eventText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonSerializer.Deserialize<DiagnosticEvent>(line, Serialization.Options)!).ToArray();
    Check(transitionEvents.Count(e => e.EventName == "start.wait_finished" && e.DurationMs >= 1500) == 2,
        "O inicio nao aguardou a gravacao lenta.");
    Check(!transitionEvents.Any(e => e.Details?.GetValueOrDefault("controlKey")?.ToString() == "start.confirm"),
        "Uma transicao direta foi confundida com uma confirmacao.");
    Console.WriteLine("PASS: inicio direto com e sem dialogo alheio aguarda a gravacao sem procurar confirmacao ou repetir o envio.");
    foreach (var key in new[] { "issue.start", "issue.close" })
    {
        Check(transitionEvents.Count(e => e.EventName == "control.wait_finished" && e.Details?.GetValueOrDefault("controlKey")?.ToString() == key && e.DurationMs >= 1000) == 2,
            $"A espera pelo menu lento não foi registrada para {key}.");
        Check(transitionEvents.Count(e => e.EventName == "action.clicked" && e.Details?.GetValueOrDefault("controlKey")?.ToString() == key) == 2,
            $"A espera repetiu o clique da transição {key}.");
    }
    Check(transitionEvents.Count(e => e.EventName == "field.verified") == 7 * run.Floors.Count && transitionEvents.Any(e => e.EventName == "issue.fields_reused"),
        "Os campos de abertura não foram conferidos uma única vez por chamado na tentativa.");
    Check(transitionEvents.Count(e => e.EventName == "submit.ready" && e.Details?.GetValueOrDefault("controlKey")?.ToString() == "close.submit") == run.Floors.Count,
        "O fechamento não aguardou o formulário pronto para envio.");
    Console.WriteLine("PASS: cada chamado tem os campos de abertura conferidos uma vez por tentativa; as leituras seguintes conferem status e responsável.");
    Check(!eventText.Contains(profile.ReporterEmail) && !eventText.Contains(profile.FullName) && !eventText.Contains(profile.Floors[0].Resolution), "Dados do formulário vazaram para os logs.");
    Console.WriteLine("PASS: navegador registra etapas e retomadas sem valores privados do formulário.");
    Console.WriteLine("PASS: retomada após criação atribui, inicia e fecha os mesmos chamados.");
    Check(transitionEvents.Count(e => e.EventName == "assign.wait_finished" && e.DurationMs >= 1500) == 2,
        "A atribuição não aguardou a gravação lenta.");
    Console.WriteLine("PASS: atribuição otimista aguarda a gravação sem repetir cliques; responsável salvo é reconhecido mesmo com Assign to me visível.");
    Console.WriteLine("PASS: menus de início e fechamento carregam com atraso; opção desabilitada e menuitem aguardam sem repetir cliques.");
    Check(run.Floors.Select(f => f.IssueKey).Distinct().Count() == 2, "Chamados não são distintos.");
    Console.WriteLine("PASS: fluxo completo com rótulos (required) ocultos e comboboxes cobertos pelo valor selecionado.");
    Console.WriteLine("PASS: status antecipado e modal aberto/fechado aguardam a gravação lenta sem perder ou repetir o fechamento.");
    await using (var automation = new EdgeJiraAutomation(store))
    {
        await automation.ConnectAsync(run.Settings, _ => { }, default);
        foreach (var floor in run.Floors)
        {
            var snapshot = await automation.ReadIssueAsync(run, floor, default);
            Check(snapshot.Closed && snapshot.ResolutionMatches && snapshot.PublicCommentMatches && snapshot.TeamMatches && snapshot.AssignedToCurrentUser, "Fechamento divergente.");
        }
        Console.WriteLine("PASS: persistência de textos, equipe, responsável e status após reabrir o Edge.");
        Console.WriteLine("PASS: equipe de fechamento com espaços extras é escolhida e conferida pelo nome do Jira.");
        Console.WriteLine("PASS: resolução ausente da tela principal conferida no histórico e comentário público conferido no portal.");
        var otherTechnician = Serialization.Copy(run); otherTechnician.TechnicianName = "Outro Técnico";
        var otherSnapshot = await automation.ReadIssueAsync(otherTechnician, otherTechnician.Floors[1], default);
        Check(!otherSnapshot.AssignedToCurrentUser, "Responsável diferente foi aceito como técnico conectado.");
        run.Settings.TimeoutSeconds = 1;
        try { await automation.AssignAsync(otherTechnician, otherTechnician.Floors[1]); throw new Exception("Atribuição divergente foi aceita."); }
        catch (InvalidOperationException ex) when (ex.Data["DiagnosticCode"]?.ToString() == "ASSIGN_SUBMISSION_UNCONFIRMED") { }
        finally { run.Settings.TimeoutSeconds = 10; }
        Check(otherTechnician.TechnicianName == "Outro Técnico", "A atribuição substituiu a identidade conhecida por outro responsável.");
        Console.WriteLine("PASS: responsável divergente é rejeitado sem substituir a identidade conhecida do técnico.");
        var swapped = Serialization.Copy(run.Floors[0]); swapped.IssueKey = run.Floors[1].IssueKey;
        try { await automation.ReadIssueAsync(run, swapped, default); throw new Exception("Pavimento errado aceito."); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("não corresponde")) { }
        Console.WriteLine("PASS: chamado de outro pavimento é rejeitado antes de qualquer ação.");
        hidePublic = true;
        var incomplete = await automation.ReadIssueAsync(run, run.Floors[0], default);
        Check(!incomplete.PublicCommentMatches, "A resolução foi confundida com o comentário público.");
        Console.WriteLine("PASS: resolução e comentário interno não substituem a verificação do comentário público.");
        hidePublic = false; wrongLatestResolution = true;
        var changed = await automation.ReadIssueAsync(run, run.Floors[0], default);
        Check(!changed.ResolutionMatches && changed.PublicCommentMatches, "Um valor anterior da resolução foi aceito como o valor atual.");
        Console.WriteLine("PASS: a atualização mais recente da resolução prevalece sobre o texto correto de uma atualização antiga.");
    }

    var rejectedProfile = Serialization.Copy(profile); rejectedProfile.Floors = [rejectedProfile.Floors[0]];
    rejectAssignment = true;
    var assignmentProbe = ProfileValidation.CreateRun(rejectedProfile, ExampleSettings());
    assignmentProbe.Settings = Serialization.Copy(run.Settings);
    assignmentProbe.TechnicianName = run.TechnicianName;
    await engine.ExecuteAsync(assignmentProbe, default);
    var assignmentFloor = assignmentProbe.Floors[0];
    var assignmentKey = assignmentFloor.IssueKey;
    Check(assignmentProbe.State == RunState.Failed && assignmentFloor.Stage == FloorStage.Created && assignmentFloor.PendingAction == PendingAction.Assign,
        "Atribuição rejeitada foi aceita ou perdeu a pendência.");
    Check(assignmentProbe.TechnicianName == run.TechnicianName, "Uma atribuição rejeitada alterou a identidade do técnico.");
    using (var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Directory.EnumerateFiles(store.DiagnosticsDirectory(assignmentProbe.Id), "summary.json", SearchOption.AllDirectories).Single())))
        Check(summary.RootElement.GetProperty("errorCode").GetString() == "ASSIGN_VERIFICATION_FAILED" && summary.RootElement.GetProperty("failedStep").GetString() == "assign.verify",
            "Atribuição rejeitada perdeu a causa técnica ou a etapa.");
    rejectAssignment = false;
    assignmentDelayMs = 2500;
    assignmentProbe.Settings.TimeoutSeconds = 1;
    var requestsBeforeAssignmentTimeout = assignmentRequests;
    await engine.ExecuteAsync(assignmentProbe, default);
    Check(assignmentProbe.State == RunState.Failed && assignmentFloor.PendingAction == PendingAction.Assign && assignmentRequests == requestsBeforeAssignmentTimeout + 1,
        "A espera da atribuição ignorou o tempo limite, repetiu o clique ou perdeu a pendência.");
    assignmentDelayMs = 0;
    assignmentProbe.Settings.TimeoutSeconds = 10;
    var requestsBeforeAssignmentResume = assignmentRequests;
    await engine.ResumeAsync(assignmentProbe, _ => throw new Exception("Solicitou nova abertura."), default);
    Check(assignmentProbe.State == RunState.Completed && assignmentFloor.IssueKey == assignmentKey && assignmentRequests == requestsBeforeAssignmentResume + 1,
        "A retomada da atribuição não concluiu o mesmo chamado.");
    Console.WriteLine("PASS: rejeição e demora da atribuição preservam a pendência; retomada conclui o mesmo chamado.");

    rejectClosing = true;
    var rejectedRun = ProfileValidation.CreateRun(rejectedProfile, ExampleSettings());
    rejectedRun.Settings = Serialization.Copy(run.Settings); rejectedRun.Settings.TimeoutSeconds = 2;
    await engine.ExecuteAsync(rejectedRun, default);
    var rejectedKey = rejectedRun.Floors[0].IssueKey;
    Check(rejectedRun.State == RunState.NeedsReconciliation && rejectedRun.Floors[0].PendingAction == PendingAction.Close, "Envio rejeitado foi aceito ou perdeu a pendência.");
    using (var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Directory.EnumerateFiles(store.DiagnosticsDirectory(rejectedRun.Id), "summary.json", SearchOption.AllDirectories).Single())))
        Check(summary.RootElement.GetProperty("errorCode").GetString() == "CLOSE_SUBMISSION_UNCONFIRMED", "Envio não confirmado perdeu a causa técnica.");
    var requestsBeforeRetry = closeRequests;
    await engine.ResumeAsync(rejectedRun, _ => throw new Exception("Solicitou nova abertura."), default);
    Check(closeRequests == requestsBeforeRetry && rejectedRun.State == RunState.NeedsReconciliation, "Retomada reenviou comentário sem conferência.");
    rejectClosing = false;
    rejectedRun.Settings.TimeoutSeconds = 10;
    var retryConfirmations = 0;
    await engine.ResumeAsync(rejectedRun, _ => throw new Exception("Solicitou nova abertura."), default, (floor, snapshot) =>
    {
        Check(floor.IssueKey == rejectedKey && snapshot.Started && snapshot.AssignedToCurrentUser, "Conferência não corresponde ao chamado pendente.");
        retryConfirmations++; return Task.FromResult(true);
    });
    Check(rejectedRun.State == RunState.Completed && rejectedRun.Floors[0].IssueKey == rejectedKey && retryConfirmations == 1 && closeRequests == requestsBeforeRetry + 1, rejectedRun.Message);
    Console.WriteLine("PASS: envio rejeitado preserva a pendência e a conferência explícita retoma o mesmo chamado.");

    var teamProbe = ProfileValidation.CreateRun(rejectedProfile, ExampleSettings());
    teamProbe.Settings = Serialization.Copy(run.Settings); teamProbe.Settings.ClosingTeam = "Equipe Inexistente";
    var closesBeforeTeam = closeRequests;
    await engine.ExecuteAsync(teamProbe, default);
    Check(teamProbe.State == RunState.Failed && teamProbe.Floors[0].Stage == FloorStage.Started && closeRequests == closesBeforeTeam,
        "Equipe ausente do Jira foi enviada ou perdeu a etapa.");
    using (var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Directory.EnumerateFiles(store.DiagnosticsDirectory(teamProbe.Id), "summary.json", SearchOption.AllDirectories).Single())))
    {
        Check(summary.RootElement.GetProperty("errorCode").GetString() == "FIELD_OPTION_NOT_FOUND" && summary.RootElement.GetProperty("failedStep").GetString() == "close.prepare",
            "Equipe ausente do Jira perdeu a causa técnica ou a etapa.");
        Check(summary.RootElement.GetProperty("stepDurationsMs").GetProperty("close.prepare").GetDouble() < teamProbe.Settings.TimeoutSeconds * 1000,
            "Equipe ausente do Jira esperou o tempo limite.");
    }
    Console.WriteLine("PASS: equipe ausente das opções do Jira interrompe o fechamento na hora, sem envio.");

    hideHistory = true;
    var historyProbe = ProfileValidation.CreateRun(rejectedProfile, ExampleSettings());
    historyProbe.Settings = Serialization.Copy(run.Settings); historyProbe.Settings.TimeoutSeconds = 5;
    await engine.ExecuteAsync(historyProbe, default);
    Check(historyProbe.State == RunState.Completed && historyProbe.Floors[0].Stage == FloorStage.Closed, "Fechamento sem Histórico na página não foi concluído: " + historyProbe.Message);
    var historyEvents = (await Task.WhenAll(Directory.EnumerateFiles(store.DiagnosticsDirectory(historyProbe.Id), "events.jsonl", SearchOption.AllDirectories).Select(path => File.ReadAllLinesAsync(path))))
        .SelectMany(lines => lines).Where(line => line.Length > 0).Select(line => JsonSerializer.Deserialize<DiagnosticEvent>(line, Serialization.Options)!).ToArray();
    Check(historyEvents.Any(e => e.EventName == "verification.source" && e.Details?.GetValueOrDefault("source")?.ToString() == "transition_form"),
        "A resolução não foi conferida pelo formulário enviado.");
    var historyCaptures = historyEvents.Where(e => e.EventName == "artifact.saved").SelectMany(e => e.ArtifactPaths ?? []).ToArray();
    Check(historyCaptures.Length == 1 && historyCaptures[0].EndsWith("-historico-ausente.png", StringComparison.Ordinal),
        "A página sem Histórico não foi capturada no momento da conferência.");
    var historyExport = Path.Combine(Path.GetTempPath(), $"Vistora-history-export-{Guid.NewGuid():N}.zip");
    await diagnostics.ExportAsync(historyExport, historyProbe, includeScreenshots: true);
    using (var zip = System.IO.Compression.ZipFile.OpenRead(historyExport))
        Check(zip.Entries.Any(e => e.FullName.EndsWith("-historico-ausente.png", StringComparison.Ordinal)), "A captura da conferência ficou fora do pacote de diagnóstico.");
    // Sem fechamento pendente, nada prova que o formulário foi desta visita: sem Histórico, a resolução continua pedindo conferência.
    await using (var historyBrowser = new EdgeJiraAutomation(store))
    {
        await historyBrowser.ConnectAsync(historyProbe.Settings, _ => { }, default, historyProbe.Floors[0].IssueKey);
        var reread = await historyBrowser.ReadIssueAsync(historyProbe, historyProbe.Floors[0], default);
        Check(reread.Closed && !reread.ResolutionMatches && reread.PublicCommentMatches && reread.TeamMatches, "Uma releitura sem Histórico aceitou a resolução sem o formulário enviado.");
    }
    // Visita parada em "Precisa de conferência" com o chamado já fechado: a retomada conclui sem reenviar o fechamento.
    historyProbe.State = RunState.NeedsReconciliation;
    historyProbe.Floors[0].Stage = FloorStage.Started; historyProbe.Floors[0].PendingAction = PendingAction.Close;
    var closesBeforeHistoryResume = closeRequests;
    await engine.ResumeAsync(historyProbe, _ => throw new Exception("Solicitou nova abertura."), default);
    Check(historyProbe.State == RunState.Completed && historyProbe.Floors[0].Stage == FloorStage.Closed && closeRequests == closesBeforeHistoryResume,
        "A retomada de um fechamento pendente já gravado no Jira não concluiu ou reenviou o fechamento.");
    hideHistory = false;
    Console.WriteLine("PASS: sem Histórico na página, vale a resolução do fechamento pendente conferido no formulário; a captura do momento entra no pacote.");

    missingTransition = true; transitionDelayMs = 0;
    var transitionProbe = ProfileValidation.CreateRun(rejectedProfile, ExampleSettings());
    transitionProbe.Settings = Serialization.Copy(run.Settings); transitionProbe.Settings.TimeoutSeconds = 1;
    await engine.ExecuteAsync(transitionProbe, default);
    var transitionKey = transitionProbe.Floors[0].IssueKey;
    Check(transitionProbe.State == RunState.Failed && transitionProbe.Floors[0].Stage == FloorStage.Assigned && transitionProbe.Floors[0].PendingAction == PendingAction.Start,
        "Ação ausente foi aceita ou perdeu o progresso pendente.");
    var transitionSummaryPath = Directory.EnumerateFiles(store.DiagnosticsDirectory(transitionProbe.Id), "summary.json", SearchOption.AllDirectories).Single();
    using (var summary = JsonDocument.Parse(await File.ReadAllTextAsync(transitionSummaryPath)))
    {
        Check(summary.RootElement.GetProperty("errorCode").GetString() == "TRANSITION_LOAD_TIMEOUT" && summary.RootElement.GetProperty("failedStep").GetString() == "start.submit",
            "Menu que não carregou perdeu a causa técnica ou a etapa da falha.");
        var elapsed = summary.RootElement.GetProperty("stepDurationsMs").GetProperty("start.submit").GetDouble();
        Check(elapsed >= 1000 && elapsed < 3000, "A espera da transição não respeitou o tempo configurado.");
    }
    Console.WriteLine("PASS: menu sem ação respeita o limite e preserva chamado, etapa e causa para retomada.");

    missingTransition = false; ambiguousTransition = true;
    transitionProbe.Settings.TimeoutSeconds = 10;
    await engine.ExecuteAsync(transitionProbe, default);
    transitionSummaryPath = Directory.EnumerateFiles(store.DiagnosticsDirectory(transitionProbe.Id), "summary.json", SearchOption.AllDirectories).OrderBy(File.GetLastWriteTimeUtc).Last();
    using (var summary = JsonDocument.Parse(await File.ReadAllTextAsync(transitionSummaryPath)))
        Check(summary.RootElement.GetProperty("errorCode").GetString() == "FIELD_AMBIGUOUS" && transitionProbe.Floors[0].Stage == FloorStage.Assigned,
            "Menu com ações duplicadas executou uma transição ou perdeu a causa técnica.");
    Console.WriteLine("PASS: menu com ações duplicadas interrompe antes do clique.");

    ambiguousTransition = false; transitionDelayMs = 1200;
    transitionProbe.Settings.SelectorOverrides["issue.start"] = "#start";
    await using (var transitionBrowser = new EdgeJiraAutomation(store))
    {
        await transitionBrowser.ConnectAsync(transitionProbe.Settings, _ => { }, default, transitionKey);
        await transitionBrowser.StartAsync(transitionProbe, transitionProbe.Floors[0]);
        missingTransition = true;
        await transitionBrowser.ReadIssueAsync(transitionProbe, transitionProbe.Floors[0], default);
        using var stopWaiting = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try { await transitionBrowser.PrepareCloseAsync(transitionProbe, transitionProbe.Floors[0], stopWaiting.Token); throw new Exception("Cancelamento da espera ignorado."); }
        catch (OperationCanceledException) when (stopWaiting.IsCancellationRequested) { }
    }
    missingTransition = false;
    var closeRequestsBeforeResume = closeRequests;
    await engine.ExecuteAsync(transitionProbe, default);
    Check(transitionProbe.State == RunState.Completed && transitionProbe.Floors[0].IssueKey == transitionKey && closeRequests == closeRequestsBeforeResume + 1,
        "A retomada após esperar/cancelar o menu duplicou o chamado ou o fechamento.");
    Console.WriteLine("PASS: seletor configurado aguarda o início; cancelar a espera do fechamento permite retomar o mesmo chamado.");

    rejectStart = true; transitionDelayMs = 0; startDelayMs = 1500;
    var startProbe = ProfileValidation.CreateRun(rejectedProfile, ExampleSettings());
    startProbe.Settings = Serialization.Copy(run.Settings); startProbe.Settings.TimeoutSeconds = 3;
    var startsBeforeReject = startRequests;
    await engine.ExecuteAsync(startProbe, default);
    var startKey = startProbe.Floors[0].IssueKey;
    Check(startProbe.State == RunState.Failed && startProbe.Floors[0].Stage == FloorStage.Assigned &&
        startProbe.Floors[0].PendingAction == PendingAction.Start && startRequests == startsBeforeReject + 1,
        "Inicio rejeitado foi aceito, reenviado ou perdeu a pendencia.");
    var startSummaryPath = Directory.EnumerateFiles(store.DiagnosticsDirectory(startProbe.Id), "summary.json", SearchOption.AllDirectories).Single();
    using (var summary = JsonDocument.Parse(await File.ReadAllTextAsync(startSummaryPath)))
        Check(summary.RootElement.GetProperty("errorCode").GetString() == "START_SUBMISSION_UNCONFIRMED" &&
            summary.RootElement.GetProperty("failedStep").GetString() == "start.submit", "Inicio incerto perdeu a causa tecnica.");
    rejectStart = false; startMode = "confirm"; startProbe.Settings.TimeoutSeconds = 10;
    await engine.ExecuteAsync(startProbe, default);
    Check(startProbe.State == RunState.Completed && startProbe.Floors[0].IssueKey == startKey && startRequests == startsBeforeReject + 2,
        "Confirmacao atrasada nao retomou o mesmo chamado com um unico envio.");
    var startEvents = string.Concat(await Task.WhenAll(Directory.EnumerateFiles(store.DiagnosticsDirectory(startProbe.Id), "events.jsonl", SearchOption.AllDirectories)
        .Select(path => File.ReadAllTextAsync(path))));
    Check(startEvents.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<DiagnosticEvent>(line, Serialization.Options)!)
        .Count(e => e.EventName == "action.clicked" && e.Details?.GetValueOrDefault("controlKey")?.ToString() == "start.confirm") == 1,
        "Confirmacao do inicio foi ignorada ou clicada mais de uma vez.");
    startMode = "direct";
    Console.WriteLine("PASS: inicio rejeitado preserva pendencia; retomada aguarda confirmacao atrasada e habilitada sem duplicar chamado ou envio.");

    disabledSubmit = true;
    var submitProbe = ProfileValidation.CreateRun(rejectedProfile, ExampleSettings());
    submitProbe.Settings = Serialization.Copy(run.Settings); submitProbe.Settings.TimeoutSeconds = 1;
    var portalVisitsBeforeSubmitProbe = portalVisits;
    await engine.ExecuteAsync(submitProbe, default);
    Check(submitProbe.State == RunState.Failed && submitProbe.Floors[0].IssueKey is null && submitProbe.Floors[0].PendingAction == PendingAction.None &&
        portalVisits == portalVisitsBeforeSubmitProbe + 1, "Formulário com envio desabilitado foi enviado ou deixou uma abertura pendente.");
    using (var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Directory.EnumerateFiles(store.DiagnosticsDirectory(submitProbe.Id), "summary.json", SearchOption.AllDirectories).Single())))
        Check(summary.RootElement.GetProperty("errorCode").GetString() == "SUBMIT_NOT_READY" && summary.RootElement.GetProperty("failedStep").GetString() == "create.prepare",
            "Envio indisponível perdeu a causa técnica ou a etapa.");
    disabledSubmit = false;
    Console.WriteLine("PASS: botão de envio desabilitado interrompe antes da abertura, sem pendência a conferir.");

    // O Jira de outra unidade pode usar outros nomes no fluxo e outro endereço para o chamado.
    workflow = new JiraWorkflow
    {
        WaitingStatus = "Aguardando Campo", InProgressStatus = "Em visita", ClosedStatuses = ["  Resolvido "], RequestType = "Visita Técnica",
        StartTransition = "Começar visita", CloseTransition = "Resolver visita", IssuePath = "/browse/{key}"
    };
    wrongLatestResolution = false; transitionDelayMs = 0; startMode = "direct";
    var workflowProbe = ProfileValidation.CreateRun(profile, ExampleSettings());
    workflowProbe.Settings = Serialization.Copy(run.Settings); workflowProbe.Settings.Jira = Serialization.Copy(workflow);
    await engine.ExecuteAsync(workflowProbe, default);
    Check(workflowProbe.State == RunState.Completed && workflowProbe.Floors.All(f => f.Stage == FloorStage.Closed), "Fluxo com outros nomes não concluiu: " + workflowProbe.Message);
    await using (var workflowBrowser = new EdgeJiraAutomation(store))
    {
        await workflowBrowser.ConnectAsync(workflowProbe.Settings, _ => { }, default, workflowProbe.Floors[0].IssueKey);
        foreach (var floor in workflowProbe.Floors)
        {
            var snapshot = await workflowBrowser.ReadIssueAsync(workflowProbe, floor, default);
            Check(snapshot.Closed && snapshot.Status == "Resolvido" && snapshot.ResolutionMatches && snapshot.PublicCommentMatches && snapshot.TeamMatches,
                "Fechamento com outros nomes de status não foi conferido pelo endereço configurado.");
        }
    }
    var defaultNames = Serialization.Copy(workflowProbe.Settings); defaultNames.Jira = ExampleWorkflow(); defaultNames.TimeoutSeconds = 2;
    await using (var defaultBrowser = new EdgeJiraAutomation(store))
    {
        try { await defaultBrowser.ConnectAsync(defaultNames, _ => { }, default, workflowProbe.Floors[0].IssueKey); throw new Exception("Status de outro fluxo reconhecido com os nomes padrão."); }
        catch (InvalidOperationException ex) when (ex.Data["DiagnosticCode"]?.ToString() == "FORM_LOAD_TIMEOUT") { }
    }
    workflow = ExampleWorkflow();
    Console.WriteLine("PASS: status, transições, tipo de solicitação e endereço do chamado vêm da seção Jira das configurações.");

    loginMode = true;
    var loginProbe = ProfileValidation.CreateRun(profile, ExampleSettings()); loginProbe.Settings = Serialization.Copy(run.Settings);
    var loginDiagnostics = new DiagnosticAttempt(diagnostics, loginProbe, "access");
    using (var loginCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
    await using (var loginBrowser = new EdgeJiraAutomation(store))
    {
        loginBrowser.SetDiagnostics(loginDiagnostics);
        var loginReported = false;
        try
        {
            await loginBrowser.ConnectAsync(run.Settings, message =>
            {
                if (message.StartsWith("Faça login", StringComparison.Ordinal)) { loginReported = true; loginCancellation.Cancel(); }
            }, loginCancellation.Token);
            throw new Exception("Uma página de login foi reconhecida como formulário pronto.");
        }
        catch (OperationCanceledException) { Check(loginReported, "Não houve identificação da espera pelo login."); }
        await loginBrowser.CaptureFailureAsync(loginProbe);
    }
    await loginDiagnostics.FinishAsync(false, "Interrupted");
    Check(!Directory.EnumerateFiles(store.DiagnosticsDirectory(loginProbe.Id), "*.png", SearchOption.AllDirectories).Any(), "Página de login foi capturada.");
    Console.WriteLine("PASS: espera pelo login é medida e a captura de autenticação é impedida.");
    Console.WriteLine("PASS: a página de login continua aguardando autenticação.");

    loginMode = false; incompleteForm = true;
    var formProbe = ProfileValidation.CreateRun(profile, ExampleSettings()); formProbe.Settings = Serialization.Copy(run.Settings);
    var formDiagnostics = new DiagnosticAttempt(diagnostics, formProbe, "access");
    await using (var formBrowser = new EdgeJiraAutomation(store))
    {
        formBrowser.SetDiagnostics(formDiagnostics);
        var formSettings = Serialization.Copy(run.Settings); formSettings.TimeoutSeconds = 1;
        var messages = new List<string>();
        try { await formDiagnostics.StepAsync("browser.connect", null, () => formBrowser.ConnectAsync(formSettings, messages.Add, default)); throw new Exception("Formulário incompleto aceito."); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("campos ainda não foram reconhecidos", StringComparison.Ordinal)) { }
        Check(messages.All(m => !m.StartsWith("Faça login", StringComparison.Ordinal)), "Uma falha no formulário foi reportada como falta de login.");
        await formBrowser.CaptureFailureAsync(formProbe); await formBrowser.CaptureFailureAsync(formProbe);
    }
    await formDiagnostics.FinishAsync(false, "Failed");
    Check(Directory.EnumerateFiles(store.DiagnosticsDirectory(formProbe.Id), "*.png", SearchOption.AllDirectories).Count() == 2, "Uma captura sobrescreveu a anterior.");
    var formSummaryText = await File.ReadAllTextAsync(Path.Combine(store.DiagnosticsDirectory(formProbe.Id), "attempts", formDiagnostics.Id, "summary.json"));
    using (var formSummary = JsonDocument.Parse(formSummaryText))
        Check(formSummary.RootElement.GetProperty("errorCode").GetString() == "FORM_LOAD_TIMEOUT" &&
            formSummary.RootElement.GetProperty("stepDurationsMs").GetProperty("browser.form_loading").GetDouble() > 0,
            "Timeout ou duração do carregamento não registrados.");
    var exportPath = Path.Combine(Path.GetTempPath(), $"Vistora-browser-export-{Guid.NewGuid():N}.zip");
    await diagnostics.ExportAsync(exportPath, run);
    using (var zip = System.IO.Compression.ZipFile.OpenRead(exportPath))
    {
        var environmentFiles = zip.Entries.Where(e => e.FullName.EndsWith("environment.json")).ToArray();
        foreach (var entry in environmentFiles)
        {
            using var source = entry.Open(); using var json = await JsonDocument.ParseAsync(source);
            Check(json.RootElement.GetProperty("edgeVersion").GetString() != "indisponível", "Versão real do Edge não registrada.");
        }
        Check(environmentFiles.Length == 2, "Metadados de cada retomada ausentes.");
    }
    Console.WriteLine("PASS: capturas preservadas e ZIP inclui versões do Edge por tentativa.");
    Console.WriteLine("PASS: formulário incompleto respeita o tempo da etapa e informa o problema correto.");
    incompleteForm = false;
    var ambiguousProbe = ProfileValidation.CreateRun(profile, ExampleSettings()); ambiguousProbe.Settings = Serialization.Copy(run.Settings);
    ambiguousProbe.Settings.SelectorOverrides["portal.ready"] = "body";
    ambiguousProbe.Settings.SelectorOverrides["portal.title"] = "input";
    await engine.ExecuteAsync(ambiguousProbe, default);
    using (var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Directory.EnumerateFiles(store.DiagnosticsDirectory(ambiguousProbe.Id), "summary.json", SearchOption.AllDirectories).Single())))
        Check(summary.RootElement.GetProperty("errorCode").GetString() == "FIELD_AMBIGUOUS" && ambiguousProbe.Floors.All(f => f.IssueKey is null),
            "Controle ambíguo não identificado antes do envio.");
    Console.WriteLine("PASS: controle ambíguo registra chave, contagem e causa sem enviar o formulário.");
    passwordForm = true;
    var passwordProbe = ProfileValidation.CreateRun(profile, ExampleSettings()); passwordProbe.Settings = Serialization.Copy(run.Settings);
    passwordProbe.Settings.SelectorOverrides["portal.ready"] = "body";
    var passwordDiagnostics = new DiagnosticAttempt(diagnostics, passwordProbe, "access");
    await using (var passwordBrowser = new EdgeJiraAutomation(store))
    {
        passwordBrowser.SetDiagnostics(passwordDiagnostics);
        await passwordBrowser.ConnectAsync(passwordProbe.Settings, _ => { }, default);
        await passwordBrowser.CaptureFailureAsync(passwordProbe);
    }
    await passwordDiagnostics.FinishAsync(false, "Interrupted");
    Check(!Directory.EnumerateFiles(store.DiagnosticsDirectory(passwordProbe.Id), "*.png", SearchOption.AllDirectories).Any(), "Formulário de senha fora de rota de login foi capturado.");
    Console.WriteLine("PASS: formulário de senha impede captura mesmo fora de uma rota de autenticação.");
}
catch (Exception ex) { failures++; Console.WriteLine("FAIL: " + ex); Console.WriteLine("Diagnóstico: " + store.DiagnosticsDirectory(run.Id)); }
finally { await app.StopAsync(); await app.DisposeAsync(); }
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

static class Fixture
{
    // Equipe e fluxo de exemplo, que os padrões deixam em branco. O chamado chama o setor de "Local".
    public static AppSettings ExampleSettings() => new()
    {
        ClosingTeam = "Equipe de Campo", FieldLabels = new() { ["sector"] = "Setor | Local" },
        Jira = new() { WaitingStatus = "Aguardando atendimento", InProgressStatus = "Em atendimento", RequestType = "Visita preventiva",
            StartTransition = "Iniciar atendimento", CloseTransition = "Fechar chamado" }
    };

    public static string Html(bool hidePublic, bool wrongLatestResolution, int transitionDelayMs, bool missingTransition, bool ambiguousTransition, string startMode,
        bool disabledSubmit, bool hideHistory, JiraWorkflow workflow) => """
    <!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><title>Jira — ambiente local de teste</title>
    <style>body{font:16px Segoe UI;padding:28px;background:#f6f8fb;color:#123743}main{max-width:850px;margin:auto;background:white;padding:24px}label{display:block;margin:14px 0 4px}input,textarea,select{font:inherit;padding:8px;width:90%}button{padding:10px;margin:8px}dl>div{display:flex;gap:24px;padding:8px;border-bottom:1px solid #ddd}dl>div>span:first-child{width:250px}dialog{width:650px}pre{white-space:pre-wrap}.options{background:#eef4f4}.options>div{padding:10px;cursor:pointer}.sr-only{position:absolute;width:1px;height:1px;padding:0;overflow:hidden;clip:rect(0,0,0,0);white-space:nowrap}.combo-control{position:relative}.selected-overlay{position:absolute;inset:0;background:#eef4f4;z-index:2;padding:8px;width:90%}</style></head><body><main id="main"></main>
    <script>
    const hidePublic = __HIDE_PUBLIC__;
    const wrongLatestResolution = __WRONG_LATEST_RESOLUTION__;
    const transitionDelayMs = __TRANSITION_DELAY_MS__;
    const missingTransition = __MISSING_TRANSITION__;
    const ambiguousTransition = __AMBIGUOUS_TRANSITION__;
    const startMode = '__START_MODE__';
    const disabledSubmit = __DISABLED_SUBMIT__;
    // Jira sem Histórico na atividade e sem a resolução no layout principal.
    const hideHistory = __HIDE_HISTORY__;
    // Nomes do fluxo como o Jira exibe; um fluxo diferente do padrão também tira o data-testid do status.
    const workflow = __WORKFLOW__;
    const main=document.getElementById('main'); const issues=JSON.parse(localStorage.getItem('issues')||'{}');
    const issueKey=location.pathname.match(/SD-\d+/)?.[0];
    const escape=s=>String(s||'').replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('"','&quot;');
    function persist(){localStorage.setItem('issues',JSON.stringify(issues))}
    // Simula um formulário que só processa os valores depois de sair do campo.
    function delayedValidation(id){
      const input=document.getElementById(id);let pending;
      input.dataset.committed='';
      input.addEventListener('input',()=>{clearTimeout(pending);input.dataset.committed=''});
      input.addEventListener('blur',()=>{const value=input.value||input.innerText;pending=setTimeout(()=>input.dataset.committed=value,300)});
    }
    function row(label,value){return `<div><span>${escape(label)}</span><span>${escape(value)}</span></div>`}
    function historyItem(field,oldValue,newValue){return `<div data-testid="issue-history.ui.history-items.generic-history-item.history-item"><div></div><div><div>Updated the <span>${escape(field)}</span></div><div><div>${escape(oldValue)}</div><div></div><div><span>${escape(newValue)}</span></div></div></div></div>`}
    function combo(id,label,option){return `<label for="${id}">${label}</label><div class="combo-control"><span id="react-select-${id}-single-value" class="selected-overlay">Selecione</span><input id="${id}" role="combobox" aria-describedby="react-select-${id}-single-value" autocomplete="off"></div><div id="${id}options" class="options"></div>`}
    if(issueKey&&location.pathname.includes('/servicedesk/customer/portal/')){
      const issue=issues[issueKey];
      main.innerHTML=`<p>${issueKey}</p><h1>${escape(issue.title)}</h1><section><h2>Descrição</h2><pre>${escape(issue.resolution)}</pre></section><div class="ak-renderer-wrapper is-comment"><div class="ak-renderer-document">${escape(hidePublic?'Outro comentário público':issue.comment)}</div></div>`;
    }else if(!issueKey){
      main.innerHTML=`<h1>${escape(workflow.requestType)}</h1><form id="form">${combo('reporter','Solicitante*','')}
      <label for="title">Título*</label><input id="title" required>
      <label for="extension">Ramal*</label><input id="extension" required>
      <label for="fullName">Nome completo*</label><input id="fullName" required>
      ${combo('unit','Unidade*','')}<label for="floor">Andar*</label><input id="floor" required>
      <label for="sector">Setor*</label><input id="sector" required><label for="room">Sala*</label><input id="room" required>
      <label for="phone">Telefone</label><input id="phone"><label for="description-label-target">Descrição*</label><div id="description" role="textbox" aria-label="Descrição" contenteditable="true" style="min-height:80px;border:1px solid #ccc"></div>
      <button type="submit" ${disabledSubmit?'disabled':''}>Enviar</button></form>`;
      for(const label of main.querySelectorAll('label')){
        if(label.textContent.endsWith('*')){const suffix=document.createElement('span');suffix.className='sr-only';suffix.textContent='(required)';label.appendChild(suffix)}
      }
      const hidden=document.createElement('div');hidden.style.display='none';hidden.innerHTML='<label for="hidden-reporter">Solicitante*(required)</label><input id="hidden-reporter" role="combobox">';main.appendChild(hidden);
      for(const [id,value] of [['reporter','Pessoa Teste pessoa@example.com'],['unit','Unidade A']]){
        const input=document.getElementById(id),options=document.getElementById(id+'options');
        const selected=document.getElementById('react-select-'+id+'-single-value');
        input.addEventListener('input',()=>{
          selected.style.display='none';
          const values=id==='reporter'?['Pessoa Teste pessoa@example.com.evil',value,'Pessoa Teste pessoa2@example.com']:[value];
          options.innerHTML=values.map(v=>`<div role="option">${escape(v)}</div>`).join('');
          for(const [index,option] of Array.from(options.children).entries())option.onclick=()=>{input.value='';input.dataset.selected=values[index];selected.textContent=values[index];selected.style.display='';options.innerHTML=''};
        });
      }
      for(const id of ['title','description'])delayedValidation(id);
      document.getElementById('form').onsubmit=e=>{e.preventDefault();for(const id of ['title','description']){const input=document.getElementById(id);if(input.dataset.committed!==(input.value||input.innerText))throw Error('Jira ainda não processou '+id)}const data={};for(const id of ['reporter','title','extension','fullName','unit','floor','sector','room','phone','description']){const input=document.getElementById(id);data[id]=input.dataset.selected||input.value||input.innerText}
      const key='SD-'+(1001+Object.keys(issues).length);issues[key]={...data,status:workflow.waiting,assignee:'Unassigned',resolution:'',comment:'',team:'',commentCount:0};persist();location.href='/jira/servicedesk/projects/SD/queues/issue/'+key};
    }else{
      const issue=issues[issueKey];if(!issue){main.innerHTML='<h1>Chamado não encontrado</h1>'}else{
      // Perfil novo do Edge: o aviso de cookies da Atlassian fica aberto como diálogo.
      const cookies=document.createElement('div');cookies.setAttribute('role','dialog');cookies.setAttribute('aria-label','Aviso de cookies');cookies.innerHTML='<p>A Atlassian usa cookies.</p><button>Apenas o necessário</button><button>Aceitar tudo</button>';document.body.prepend(cookies);
      main.innerHTML=`<p>${issueKey}</p><h1>${escape(issue.title)}</h1><p>${escape(issue.reporter)}</p><button id="status" aria-label="${issue.status} - Change status" ${issueKey.endsWith('2')||workflow.plainStatus?'':'data-testid="issue-field-status.ui.status-view.status-button.status-button"'} hidden>${issue.status}</button><button aria-label="${issue.status} - Change status" data-testid="issue-field-status.ui.status-view.status-button.status-button" hidden>${issue.status}</button><div id="menu"></div><dl>
      ${row('Ramal',issue.extension)}${row('Nome completo',issue.fullName)}${row('Unidade',issue.unit)}${row('Andar',issue.floor)}${row('Local',issue.sector)}${row('Sala',issue.room)}${row('Request Type',workflow.requestType)}${row('Reporter','Pessoa Teste')}
      <div><span>Assignee</span><span><span id="assignee-value">${escape(issue.assignee)}</span>${issue.assignee==='Unassigned'||issueKey.endsWith('2')?'<a id="assign" href="#" style="display:block">Assign to me</a>':''}</span></div>
      ${row('Equipe',issue.team)}${issueKey.endsWith('1')||hideHistory?'':row('Resolução do atendimento',issue.resolution)}</dl>
      <section id="comments">${issue.comment?`<article><b>${hidePublic?'Internal note':'Comentário público'}</b><pre>${escape(issue.comment)}</pre></article>`:''}</section>`;
      // A atividade chega depois dos campos, com Comentários e Histórico como botões e não como abas.
      if(issueKey.endsWith('1')||hideHistory)setTimeout(()=>{
        const activity=document.createElement('section');activity.innerHTML=`<a href="/servicedesk/customer/portal/1/${issueKey}" target="_blank">View request in portal</a><button id="comments-tab">Comentários</button>${hideHistory?'':'<button id="history-tab">Histórico</button>'}<div role="tabpanel" id="activity-panel"></div>`;main.appendChild(activity);
        if(!hideHistory)document.getElementById('history-tab').onclick=()=>{const panel=document.getElementById('activity-panel');panel.innerHTML='<p>Carregando</p>';setTimeout(()=>{panel.innerHTML=`<div data-testid="issue-history.ui.feed-container">${historyItem('Resolution','None','Concluída')}${historyItem('Resolução do atendimento',wrongLatestResolution?issue.resolution:'None',wrongLatestResolution?'Resolução modificada':issue.resolution)}${wrongLatestResolution?historyItem('Resolução do atendimento','None',issue.resolution):''}</div>`},400)};
        document.getElementById('comments-tab').onclick=()=>document.getElementById('activity-panel').innerHTML='';
      },900);
      document.getElementById('assign')?.addEventListener('click',async e=>{
        e.preventDefault();
        // A interface antecipa o responsável; recarregar antes da resposta perde a gravação.
        const assign=document.getElementById('assign');assign.hidden=true;
        const field=document.getElementById('assignee-value');
        field.textContent='Técnico Teste';
        const response=await fetch('/fixture/assign',{method:'POST'});
        if(!response.ok)return;
        issue.assignee='Técnico Teste';persist();location.reload();
      });
      setTimeout(()=>document.getElementById('status').hidden=false,700);
      document.getElementById('status').onclick=()=>{
        const menu=document.getElementById('menu');
        menu.innerHTML='<p>-</p><p>View workflow</p>';
        setTimeout(()=>{
        if(missingTransition)return;
        if(issue.status===workflow.waiting){menu.innerHTML=`<button id="start" disabled>${escape(workflow.start)}</button>`;if(ambiguousTransition){menu.innerHTML+=`<button>${escape(workflow.start)}</button>`;return}const start=document.getElementById('start');if(issueKey.endsWith('2'))start.setAttribute('role','menuitem');setTimeout(()=>start.disabled=false,400);start.onclick=()=>{
          menu.innerHTML='';
          const submitStart=async()=>{
            const status=document.getElementById('status');status.textContent=workflow.inProgress;status.disabled=true;
            const response=await fetch('/fixture/start',{method:'POST'});
            status.disabled=false;
            if(!response.ok){status.textContent=issue.status;return}
            issue.status=workflow.inProgress;persist();location.reload();
          };
          if(startMode==='unrelated'&&issueKey.endsWith('1')){
            const unrelated=document.createElement('div');unrelated.setAttribute('role','dialog');unrelated.textContent='Atualizando chamado';document.body.appendChild(unrelated);
            submitStart();
          }else if(startMode==='confirm'){
            // O painel do chamado tambem pode ser um dialogo. A confirmacao chega depois.
            main.setAttribute('role','dialog');
            setTimeout(()=>{
              const dialog=document.createElement('dialog');dialog.setAttribute('role','dialog');dialog.innerHTML=`<h2>${escape(workflow.start)}</h2><button id="start-confirm" disabled>Confirmar</button>`;document.body.appendChild(dialog);dialog.showModal();
              const confirm=document.getElementById('start-confirm');setTimeout(()=>confirm.disabled=false,500);
              confirm.onclick=()=>{confirm.disabled=true;dialog.close();submitStart()};
            },900);
          }else submitStart();
        }}
        else if(issue.status===workflow.inProgress){menu.innerHTML=`<button id="close">${escape(workflow.close)}</button>`;document.getElementById('close').onclick=()=>{
          // O Jira antecipa o status e só depois carrega o formulário da transição.
          menu.innerHTML='';document.getElementById('status').textContent=workflow.closed;
          setTimeout(()=>{
          const dialog=document.createElement('dialog');dialog.setAttribute('role','dialog');dialog.innerHTML=`<h2>${escape(workflow.close)}</h2><label for="resolution">Resolução do atendimento</label><textarea id="resolution"></textarea><button id="reply" type="button">Reply to customer</button><label for="public">Comentário público</label><textarea id="public"></textarea><label for="team">Equipe</label><select id="team"><option>Selecione</option><option>Equipe de Campo</option></select><button id="confirm">${escape(workflow.close)}</button>`;document.body.appendChild(dialog);dialog.showModal();
          document.getElementById('reply').onclick=()=>dialog.dataset.public='true';
          for(const id of ['resolution','public'])delayedValidation(id);
          document.getElementById('confirm').onclick=async()=>{for(const id of ['resolution','public']){const input=document.getElementById(id);if(input.dataset.committed!==input.value)throw Error('Jira ainda não processou '+id)}if(dialog.dataset.public!=='true')throw Error('Comentário não é público');const resolution=document.getElementById('resolution').value,comment=document.getElementById('public').value,team=document.getElementById('team').value;
          // O status muda antes da resposta do servidor. Navegar agora perde o fechamento.
          document.getElementById('status').textContent=workflow.closed;if(issueKey.endsWith('1'))dialog.close();
          const response=await fetch('/fixture/close',{method:'POST'});if(!response.ok){document.getElementById('status').textContent=issue.status;if(!dialog.open)dialog.showModal();return}issue.resolution=resolution;issue.comment=comment;issue.team=team;issue.commentCount++;issue.status=workflow.closed;persist();location.reload()}
          },600);
        }};
        },transitionDelayMs);
      }
      }
    }
    </script></body></html>
    """.Replace("__HIDE_PUBLIC__", hidePublic ? "true" : "false").Replace("__WRONG_LATEST_RESOLUTION__", wrongLatestResolution ? "true" : "false")
        .Replace("__TRANSITION_DELAY_MS__", transitionDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture)).Replace("__START_MODE__", startMode)
        .Replace("__MISSING_TRANSITION__", missingTransition ? "true" : "false").Replace("__AMBIGUOUS_TRANSITION__", ambiguousTransition ? "true" : "false")
        .Replace("__DISABLED_SUBMIT__", disabledSubmit ? "true" : "false").Replace("__HIDE_HISTORY__", hideHistory ? "true" : "false").Replace("__WORKFLOW__", JsonSerializer.Serialize(new
        {
            waiting = workflow.WaitingStatus.Trim(), inProgress = workflow.InProgressStatus.Trim(), closed = workflow.ClosedStatuses[0].Trim(),
            requestType = workflow.RequestType, start = workflow.StartTransition, close = workflow.CloseTransition,
            plainStatus = JsonSerializer.Serialize(workflow) != JsonSerializer.Serialize(ExampleSettings().Jira)
        }));
}
