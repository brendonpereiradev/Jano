namespace Vistora.Core;

// Uma tentativa vazia não deve esconder a visita que já tem chamados no Jira.
public static class VisitRunSelection
{
    public static List<VisitRun> Pending(IEnumerable<VisitRun> runs) => runs
        .Where(r => r.State != RunState.Completed)
        .OrderByDescending(HasProgress).ThenByDescending(r => r.CreatedAt).ToList();

    public static VisitRun? PendingForProfile(IEnumerable<VisitRun> runs, string profileId)
    {
        var pending = runs.Where(r => r.Profile.Id == profileId && r.State != RunState.Completed).ToList();
        var withIssues = pending.Where(r => r.Floors.Any(f => f.IssueKey is not null)).ToList();
        if (withIssues.Count > 1)
            throw new InvalidOperationException("Este perfil tem mais de uma visita pendente com chamados. Selecione a visita pendente em Executar visita antes de retomar.");
        if (withIssues.Count == 1) return withIssues[0];
        var withProgress = pending.Where(HasProgress).ToList();
        if (withProgress.Count > 1)
            throw new InvalidOperationException("Este perfil tem mais de uma abertura que precisa de conferência. Selecione a visita pendente em Executar visita; nenhum novo chamado foi aberto.");
        return withProgress.SingleOrDefault() ?? pending.OrderBy(r => r.CreatedAt).FirstOrDefault();
    }

    public static VisitRun ForResume(IEnumerable<VisitRun> runs, VisitRun selected)
    {
        if (selected.State == RunState.Completed) throw new InvalidOperationException("Esta execução já foi concluída.");
        return HasProgress(selected) ? selected : PendingForProfile(runs, selected.Profile.Id) ?? selected;
    }

    // A visita retomada usa as configurações atuais, para que um valor corrigido em Configurações
    // valha sem descartá-la. A equipe só muda antes do primeiro fechamento: os já enviados são
    // conferidos com a equipe que receberam.
    public static void ApplySettings(VisitRun run, AppSettings current)
    {
        var team = run.Settings.ClosingTeam;
        run.Settings = Serialization.Copy(current);
        if (run.Floors.Any(f => f.Stage == FloorStage.Closed || f.PendingAction == PendingAction.Close))
            run.Settings.ClosingTeam = team;
    }

    // Sem chamado nem ação em andamento, nada ficou no Jira e a visita pode ser apagada sem conferência.
    public static bool CanDiscard(VisitRun run) => run.State != RunState.Completed &&
        run.Floors.All(f => f.IssueKey is null && f.PendingAction == PendingAction.None);

    private static bool HasProgress(VisitRun run) => run.Floors.Any(f =>
        f.IssueKey is not null || f.PendingAction != PendingAction.None || f.Stage != FloorStage.Pending);
}
