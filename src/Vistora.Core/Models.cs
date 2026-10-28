using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Vistora.Core;

public static class Serialization
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}

public sealed class Floor
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Novo pavimento";
    public bool Selected { get; set; } = true;
    public string Sector { get; set; } = "Todos os setores.";
    public string Room { get; set; } = "Todas as salas.";
    public string Resolution { get; set; } = "";
}

public sealed class VisitProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Minha unidade";
    public string ReporterName { get; set; } = "";
    public string ReporterEmail { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Extension { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Title { get; set; } = "Visita preventiva";
    public string Description { get; set; } = "Solicito visita preventiva.";
    public List<string> Attachments { get; set; } = [];
    public List<Floor> Floors { get; set; } = [];
    public override string ToString() => Name;
}

public sealed class ProfileDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string? ActiveProfileId { get; set; }
    public List<VisitProfile> Profiles { get; set; } = [];
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    // Endereços fictícios; a configuração real permanece nos dados locais do usuário.
    public string JiraUrl { get; set; } = "https://jira.example.com";
    public string PortalUrl { get; set; } = "https://jira.example.com/servicedesk/customer/portal/1/group/1/create/1";
    public string QueueUrl { get; set; } = "https://jira.example.com/jira/servicedesk/projects/SD/queues/custom/1";
    public int TimeoutSeconds { get; set; } = 60;
    public int LoginTimeoutMinutes { get; set; } = 15;
    public Dictionary<string, string> SelectorOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public const string DefaultClosingTeam = "CTS Field Services";
    private string closingTeam = DefaultClosingTeam;
    // Até a 2.0.1 o padrão era "Field Services", que não existe no Jira. Configurações e visitas
    // salvas com esse valor passam a usar o nome da equipe como o Jira exibe.
    public string ClosingTeam
    {
        get => closingTeam;
        set => closingTeam = value is not null && string.Equals(Regex.Replace(value, @"\s+", " ").Trim(), "Field Services", StringComparison.OrdinalIgnoreCase)
            ? DefaultClosingTeam : value!;
    }
    public DiagnosticOptions Diagnostics { get; set; } = new();
    // Arquivos anteriores à 2.0.0 não têm o tema e abrem no claro.
    public AppTheme Theme { get; set; } = AppTheme.Light;
    private JiraWorkflow jira = new();
    // Arquivos anteriores à 1.3.0 não têm esta seção e recebem os valores padrão.
    public JiraWorkflow Jira { get => jira; set => jira = value ?? new(); }
    private Dictionary<string, string> fieldLabels = new(StringComparer.OrdinalIgnoreCase);
    // Rótulos dos campos do formulário do Jira, por chave de JiraFields. Ausente, vale o rótulo padrão.
    public Dictionary<string, string> FieldLabels
    {
        get => fieldLabels;
        set => fieldLabels = new(value ?? [], StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record JiraField(string Key, string Description, string[] Defaults);

// Campos do formulário e do chamado que o Jano preenche e confere. Cada Jira nomeia os
// campos do seu jeito; o rótulo configurado substitui os padrões, que aceitam variações.
public static class JiraFields
{
    public static readonly JiraField[] All =
    [
        new("reporter", "Solicitante (em nome de)", ["Abrir esta requisição em nome de"]),
        new("title", "Título", ["Título"]),
        new("extension", "Ramal", ["Ramal do solicitante"]),
        new("fullName", "Nome completo", ["Nome completo do usuário"]),
        new("unit", "Unidade", ["Unidade - Usuário", "Unidade - usuário"]),
        new("floor", "Pavimento", ["Andar"]),
        new("sector", "Setor", ["Setor", "Setor/Local"]),
        new("room", "Sala", ["Sala"]),
        new("phone", "Telefone", ["Telefone"]),
        new("description", "Detalhes / justificativas", ["Detalhes / justificativas", "Detalhes / justificativas:"]),
        new("submit", "Botão de envio do formulário", ["Enviar"]),
        new("resolution", "Resolução", ["Resolução do chamado"]),
        new("team", "Equipe de fechamento", ["Equipe"])
    ];

    public static string[] Labels(AppSettings settings, string key)
    {
        var field = All.Single(f => f.Key == key);
        var configured = settings.FieldLabels.GetValueOrDefault(key);
        if (string.IsNullOrWhiteSpace(configured)) return field.Defaults;
        configured = Regex.Replace(configured, @"\s+", " ").Trim();
        // O primeiro padrão mantém as variações aceitas; outro rótulo vale sozinho.
        return string.Equals(configured, field.Defaults[0], StringComparison.OrdinalIgnoreCase) ? field.Defaults : [configured];
    }
}

// Nomes do fluxo do Jira da unidade, editáveis em Configurações.
public sealed class JiraWorkflow
{
    public string WaitingStatus { get; set; } = "Aguardando N2";
    public string InProgressStatus { get; set; } = "Em andamento N2";
    private List<string> closedStatuses = ["Fechado", "Closed", "Concluído", "Concluido"];
    public List<string> ClosedStatuses { get => closedStatuses; set => closedStatuses = value ?? []; }
    public string RequestType { get; set; } = "Visita Preventiva";
    public string StartTransition { get; set; } = "Iniciar Atendimento";
    public string CloseTransition { get; set; } = "Transitar para Fechado";
    // Caminho relativo ao endereço do Jira; {project} é o prefixo do chamado e {key}, o número completo.
    public string IssuePath { get; set; } = "/jira/servicedesk/projects/{project}/queues/issue/{key}";
}

public enum FloorStage { Pending, Created, Assigned, Started, Closed }
public enum AppTheme { Light, Dark }
public enum PendingAction { None, Create, Assign, Start, Close }
public enum RunState { Running, Interrupted, Failed, NeedsReconciliation, Completed }

public sealed class FloorRun
{
    public Floor Floor { get; set; } = new();
    public string? IssueKey { get; set; }
    public FloorStage Stage { get; set; }
    public PendingAction PendingAction { get; set; }
    public string Message { get; set; } = "Aguardando";
    [JsonIgnore] public string Name => Floor.Name;
    [JsonIgnore] public string StageLabel => Stage switch
    {
        FloorStage.Pending => "Pendente", FloorStage.Created => "Aberto", FloorStage.Assigned => "Atribuído",
        FloorStage.Started => "Em atendimento", FloorStage.Closed => "Fechado", _ => "Pendente"
    };
}

public sealed class VisitRun
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public VisitProfile Profile { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
    public string TechnicianName { get; set; } = "";
    public List<FloorRun> Floors { get; set; } = [];
    public RunState State { get; set; } = RunState.Interrupted;
    public string Message { get; set; } = "Pronta para iniciar";
    [JsonIgnore] public string ProfileName => Profile.Name;
    [JsonIgnore] public string DateLabel => CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    [JsonIgnore] public string StateLabel => State switch
    {
        RunState.Completed => "Concluída", RunState.Running => "Em execução", RunState.Interrupted => "Interrompida",
        RunState.NeedsReconciliation => "Precisa de conferência", _ => "Requer atenção"
    };
    [JsonIgnore] public string ProgressLabel => $"{Floors.Count(f => f.Stage == FloorStage.Closed)} de {Floors.Count} fechados";
}

public sealed record IssueSnapshot(string Key, string Status, bool AssignedToCurrentUser, bool Closed,
    bool Started, bool ResolutionMatches, bool PublicCommentMatches, bool TeamMatches);

public sealed class ReconciliationException(string message) : Exception(message);

public static class ProfileValidation
{
    public static List<string> Validate(VisitProfile profile, AppSettings settings)
    {
        List<string> errors = [];
        foreach (var (label, value) in new[] { ("Nome do perfil", profile.Name), ("Solicitante", profile.ReporterName),
                     ("E-mail do solicitante", profile.ReporterEmail), ("Nome completo", profile.FullName),
                     ("Ramal", profile.Extension), ("Unidade", profile.Unit), ("Título", profile.Title),
                     ("Detalhes / justificativas", profile.Description) })
            if (string.IsNullOrWhiteSpace(value)) errors.Add($"Preencha {label}.");
        if (!string.IsNullOrWhiteSpace(profile.ReporterEmail) &&
            !System.Net.Mail.MailAddress.TryCreate(profile.ReporterEmail, out _)) errors.Add("Informe um e-mail válido.");
        if (profile.Floors.Select(f => f.Id).Distinct().Count() != profile.Floors.Count)
            errors.Add("Os identificadores dos pavimentos estão duplicados.");
        if (profile.Floors.Select(f => f.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profile.Floors.Count)
            errors.Add("Use nomes diferentes para os pavimentos.");
        var floors = profile.Floors.Where(f => f.Selected).ToList();
        if (floors.Count == 0) errors.Add("Selecione pelo menos um pavimento.");
        foreach (var floor in floors)
        {
            if (string.IsNullOrWhiteSpace(floor.Name) || string.IsNullOrWhiteSpace(floor.Sector) || string.IsNullOrWhiteSpace(floor.Room))
                errors.Add("Preencha nome, setor e sala de todos os pavimentos selecionados.");
            if (string.IsNullOrWhiteSpace(floor.Resolution)) errors.Add($"Preencha a resolução de {floor.Name}.");
        }
        foreach (var path in profile.Attachments)
            if (!File.Exists(path)) errors.Add($"Anexo não encontrado: {Path.GetFileName(path)}.");
        errors.AddRange(ValidateSettings(settings));
        return errors.Distinct().ToList();
    }

    public static List<string> ValidateSettings(AppSettings settings)
    {
        List<string> errors = [];
        if (!Uri.TryCreate(settings.JiraUrl, UriKind.Absolute, out var jira) || jira.Scheme != Uri.UriSchemeHttps)
            errors.Add("O endereço do Jira deve começar com https://.");
        foreach (var url in new[] { settings.PortalUrl, settings.QueueUrl })
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps ||
                jira is null || !string.Equals(parsed.Authority, jira.Authority, StringComparison.OrdinalIgnoreCase))
                errors.Add("O formulário e a fila devem pertencer ao mesmo endereço HTTPS do Jira.");
        if (settings.TimeoutSeconds is < 10 or > 180) errors.Add("O tempo de espera deve estar entre 10 e 180 segundos.");
        if (settings.LoginTimeoutMinutes is < 1 or > 120) errors.Add("O tempo para login deve estar entre 1 e 120 minutos.");
        if (string.IsNullOrWhiteSpace(settings.ClosingTeam)) errors.Add("Preencha Equipe de fechamento.");
        var workflow = settings.Jira;
        foreach (var (label, value) in new[] { ("Tipo de solicitação", workflow.RequestType), ("Status aguardando atendimento", workflow.WaitingStatus),
                     ("Status em atendimento", workflow.InProgressStatus), ("Transição para iniciar o atendimento", workflow.StartTransition),
                     ("Transição para fechar", workflow.CloseTransition), ("Caminho do chamado", workflow.IssuePath) })
            if (string.IsNullOrWhiteSpace(value)) errors.Add($"Preencha {label}.");
        if (workflow.ClosedStatuses.Count == 0 || workflow.ClosedStatuses.Any(string.IsNullOrWhiteSpace))
            errors.Add("Informe pelo menos um status de chamado fechado, sem linhas vazias.");
        if (!string.IsNullOrWhiteSpace(workflow.IssuePath) && (!workflow.IssuePath.StartsWith('/') || !workflow.IssuePath.Contains("{key}", StringComparison.Ordinal)))
            errors.Add("O caminho do chamado deve começar com / e conter {key}.");
        foreach (var key in settings.FieldLabels.Keys)
            if (JiraFields.All.All(f => !string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"Rótulo de campo desconhecido: {key}.");
        return errors.Distinct().ToList();
    }

    public static VisitRun CreateRun(VisitProfile profile, AppSettings settings)
    {
        var errors = Validate(profile, settings);
        if (errors.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        var copy = Serialization.Copy(profile);
        return new VisitRun { Profile = copy, Settings = Serialization.Copy(settings),
            Floors = copy.Floors.Where(f => f.Selected).Select(f => new FloorRun { Floor = f }).ToList() };
    }
}
