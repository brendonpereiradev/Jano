using Vistora.Core;

namespace Vistora.Desktop;

public sealed record PendingVisitChoice(VisitRun Run, bool ProfileDeleted)
{
    public string Label => $"{Run.ProfileName}{(ProfileDeleted ? " (perfil excluído)" : "")} · {Run.DateLabel} · {Run.StateLabel} · {Run.ProgressLabel}";
}
