using System.Windows;
using System.Windows.Input;
using System.Windows.Shell;

namespace Vistora.Desktop;

/// <summary>
/// Janela com a moldura da família (como no Hermes): barra de título própria de 48 px, com o emblema, o nome,
/// o subtítulo da janela, a troca de tema e os botões da janela na mesma linha. O desenho fica em Themes/Window.xaml.
/// </summary>
public class JanoWindow : Window
{
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(JanoWindow));
    /// <summary>Texto ao lado do nome, nas janelas auxiliares (por exemplo, "Configurar perfil").</summary>
    public string? Subtitle { get => (string?)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(nameof(IsBusy), typeof(bool), typeof(JanoWindow));
    /// <summary>Trabalho em andamento: o emblema da barra de título se anima.</summary>
    public bool IsBusy { get => (bool)GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }

    public static readonly DependencyProperty ShowThemeToggleProperty = DependencyProperty.Register(nameof(ShowThemeToggle), typeof(bool), typeof(JanoWindow));
    /// <summary>Mostra a troca de tema na barra; quem liga trata <see cref="ToggleThemeCommand"/>.</summary>
    public bool ShowThemeToggle { get => (bool)GetValue(ShowThemeToggleProperty); set => SetValue(ShowThemeToggleProperty, value); }

    public static readonly RoutedCommand ToggleThemeCommand = new(nameof(ToggleThemeCommand), typeof(JanoWindow));

    public JanoWindow()
    {
        // A borda de vidro de 1 px embaixo mantém a sombra da janela; a área de arrastar é a barra inteira.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 48, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false
        });
        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (_, _) => SystemCommands.MaximizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand, (_, _) => SystemCommands.RestoreWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(this)));
    }
}
