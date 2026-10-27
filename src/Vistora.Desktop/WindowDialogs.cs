using System.Windows;
using Vistora.Core;
using Vistora.Infrastructure;

namespace Vistora.Desktop;

/// <summary>Janelas da tela principal, abertas sobre a janela que <paramref name="owner"/> devolve.</summary>
public sealed class WindowDialogs(Func<Window?> owner, DiagnosticService diagnostics) : IMainDialogs
{
    private Window? Owner => owner();

    public bool ConfirmDanger(string title, string message, string confirmLabel) =>
        new DangerConfirmWindow(title, message, confirmLabel) { Owner = Owner }.ShowDialog() == true;

    public bool Confirm(string message) =>
        Show(message, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public VisitProfile? EditProfile(VisitProfile profile)
    {
        var dialog = new ProfileWindow(profile) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Profile : null;
    }

    public string? AskIssueKey(string floorName)
    {
        var dialog = new IssueKeyWindow(floorName) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.IssueKey : null;
    }

    public bool ConfirmCloseRetry(FloorRun floor, IssueSnapshot snapshot) => Show(
        $"O fechamento anterior de {floor.Name} ({floor.IssueKey}) ficou sem confirmação. No Jira, o chamado está em “{snapshot.Status}”.\n\n" +
        "Confira na janela do Edge a resolução e os comentários deste chamado antes de continuar. Se o texto já foi enviado, escolha Não e conclua o fechamento no Jira para evitar duplicação.\n\n" +
        "Você conferiu que a resolução e o comentário público não foram enviados e deseja tentar o fechamento novamente?",
        MessageBoxButton.YesNo, MessageBoxImage.Question, "Conferir fechamento pendente", MessageBoxResult.No) == MessageBoxResult.Yes;

    public void ShowWarning(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Warning);
    public void ShowError(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Error);

    public Task ExportLogAsync(VisitRun? run) => DiagnosticExportUi.ExportAsync(Owner!, diagnostics, run);

    private MessageBoxResult Show(string message, MessageBoxButton buttons, MessageBoxImage image, string title = "",
        MessageBoxResult defaultResult = MessageBoxResult.None) => MessageWindow.Show(Owner, message, title, buttons, image, defaultResult);
}
