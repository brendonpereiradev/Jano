using System.Diagnostics;
using Microsoft.Playwright;

namespace Vistora.Automation.Edge;

// Acompanha as requisições de gravação enviadas ao Jira enquanto uma ação está em andamento.
// A interface do Jira é otimista: navegar antes do fim dessas requisições pode cancelar a gravação.
internal sealed class MutationTracker : IDisposable
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(500);
    private readonly IPage page;
    private readonly string authority;
    private readonly HashSet<IRequest> pending = [];
    private readonly object sync = new();
    private long lastActivity = Stopwatch.GetTimestamp();
    private int count;

    public MutationTracker(IPage page, string jiraUrl)
    {
        this.page = page;
        authority = new Uri(jiraUrl).Authority;
        page.Request += Requested;
        page.RequestFinished += Finished;
        page.RequestFailed += Finished;
    }

    public int Count { get { lock (sync) return count; } }

    // Sem requisições pendentes e sem atividade recente, inclusive a marcada por MarkActivity.
    public bool Settled
    {
        get { lock (sync) return pending.Count == 0 && Stopwatch.GetElapsedTime(lastActivity) >= QuietPeriod; }
    }

    // Um clique pode disparar a gravação com algum atraso; o período de silêncio recomeça a partir dele.
    public void MarkActivity() { lock (sync) lastActivity = Stopwatch.GetTimestamp(); }

    private void Requested(object? sender, IRequest request)
    {
        if (request.Method is not ("POST" or "PUT" or "PATCH" or "DELETE") ||
            request.ResourceType is not ("fetch" or "xhr" or "document") ||
            !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Authority, authority, StringComparison.OrdinalIgnoreCase)) return;
        lock (sync) { pending.Add(request); count++; lastActivity = Stopwatch.GetTimestamp(); }
    }

    private void Finished(object? sender, IRequest request)
    {
        lock (sync) { if (pending.Remove(request)) lastActivity = Stopwatch.GetTimestamp(); }
    }

    public void Dispose()
    {
        page.Request -= Requested;
        page.RequestFinished -= Finished;
        page.RequestFailed -= Finished;
    }
}
