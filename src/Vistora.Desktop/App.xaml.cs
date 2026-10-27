using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using Vistora.Core;
using Vistora.Infrastructure;

namespace Vistora.Desktop;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private DiagnosticService? diagnostics;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var preview = e.Args.Contains("--preview");
        if (!preview)
        {
            instanceMutex = new Mutex(true, "Local\\Vistora.Desktop", out var created);
            if (!created) { MessageWindow.Show(null, "O Vistora já está aberto.", "Aplicativo já aberto", image: MessageBoxImage.Information); Shutdown(); return; }
        }
        diagnostics = new DiagnosticService(preview ? Path.Combine(Path.GetTempPath(), "VistoraPreview", Guid.NewGuid().ToString("N")) : null);
        DispatcherUnhandledException += (_, args) =>
        {
            diagnostics.AppEvent("app.unhandled_error", "Erro não tratado na interface.", DiagnosticLevel.Error, args.Exception, code: "UNHANDLED_UI_ERROR");
            MessageWindow.Show(MainWindow, args.Exception.Message, "Erro inesperado", image: MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            diagnostics.AppEvent("app.fatal_error", "Encerramento por erro não tratado.", DiagnosticLevel.Error,
                args.ExceptionObject as Exception, code: "FATAL_ERROR");
            Task.Run(() => diagnostics.FlushAsync()).GetAwaiter().GetResult();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            diagnostics.AppEvent("app.task_error", "Erro em tarefa de segundo plano.", DiagnosticLevel.Error, args.Exception);
        // O tema salvo vale antes da primeira janela, para ela não abrir clara e escurecer depois.
        Theme.Apply(preview ? (e.Args.Contains("--preview-dark") ? AppTheme.Dark : AppTheme.Light) : SavedTheme(diagnostics));
        var pageIndex = Array.IndexOf(e.Args, "--preview-page");
        var page = preview && pageIndex >= 0 && e.Args.Length > pageIndex + 1 && int.TryParse(e.Args[pageIndex + 1], out var n) ? n : 0;
        MainWindow? window = null;
        try
        {
            var viewModel = new MainViewModel(diagnostics, new WindowDialogs(() => window, diagnostics), preview, preview && e.Args.Contains("--preview-pending"))
                { IsDarkTheme = Theme.Current == AppTheme.Dark };
            window = new MainWindow(viewModel, page);
            window.Loaded += async (_, _) => await viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            diagnostics.AppEvent("app.startup_failed", "Não foi possível iniciar o aplicativo.", DiagnosticLevel.Error, ex, code: "STARTUP_FAILED");
            var emergency = new JanoWindow { Title = "Vistora — diagnóstico", Subtitle = "Diagnóstico", Width = 540, Height = 260, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = "Não foi possível iniciar o Vistora. Os dados locais foram preservados. Os registros disponíveis ficam na pasta de logs do aplicativo.", TextWrapping = TextWrapping.Wrap });
            emergency.Content = panel; MainWindow = emergency; emergency.Show(); return;
        }
        MainWindow = window;
        window.Show();
        if (preview)
        {
            if (e.Args.Contains("--preview-history")) window.SelectPage(2);
            if (e.Args.Contains("--preview-settings")) window.SelectPage(3);
            window.ContentRendered += async (_, _) =>
            {
                await Task.Delay(500);
                window.UpdateLayout();
                // A barra de título é desenhada pelo próprio aplicativo, então a captura pega a janela inteira.
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var pathIndex = Array.IndexOf(e.Args, "--preview");
                var path = e.Args.Length > pathIndex + 1 ? e.Args[pathIndex + 1] : "vistora-preview.png";
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                await using var file = File.Create(path);
                encoder.Save(file);
                var exportIndex = Array.IndexOf(e.Args, "--preview-export-log");
                if (exportIndex >= 0 && e.Args.Length > exportIndex + 1)
                    await diagnostics.ExportAsync(Path.GetFullPath(e.Args[exportIndex + 1]));
                Shutdown();
            };
        }
    }
    // Um settings.json ilegível não impede a abertura: o tema fica claro e o MainViewModel trata o arquivo ao carregar.
    private static AppTheme SavedTheme(DiagnosticService diagnostics)
    {
        try { return Task.Run(() => new LocalStore(diagnostics.Root).LoadSettingsAsync()).GetAwaiter().GetResult().Theme; }
        catch (Exception) { return AppTheme.Light; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (diagnostics is not null) Task.Run(async () => await diagnostics.DisposeAsync()).GetAwaiter().GetResult();
        instanceMutex?.Dispose(); base.OnExit(e);
    }
}
