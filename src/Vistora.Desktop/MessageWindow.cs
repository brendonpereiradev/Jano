using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Vistora.Desktop;

/// <summary>
/// Mensagem com a moldura e o tema do aplicativo, no lugar da MessageBox do Windows: as mesmas opções de botões
/// e o mesmo resultado. O botão padrão é o destacado; Esc e o X da janela respondem como Cancelar (ou Não, ou OK).
/// </summary>
public sealed class MessageWindow : JanoWindow
{
    internal MessageBoxResult Result { get; private set; }
    internal IReadOnlyList<Button> Buttons { get; }

    internal MessageWindow(string message, string title, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
    {
        Title = title.Length > 0 ? title : "Vistora"; Subtitle = title;
        Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var body = new Grid { Margin = new Thickness(24, 22, 24, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        var (glyph, brush) = image switch
        {
            MessageBoxImage.Error => ("", "AppDangerBrush"),
            MessageBoxImage.Warning => ("", "AppAttentionBrush"),
            MessageBoxImage.Question => ("", "AppBrandAccentBrush"),
            MessageBoxImage.Information => ("", "AppBrandAccentBrush"),
            _ => ((string?)null, (string?)null)
        };
        if (glyph is not null)
        {
            var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 22, Margin = new Thickness(0, 0, 16, 0) };
            icon.SetResourceReference(TextBlock.ForegroundProperty, brush);
            body.Children.Add(icon);
        }
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1); body.Children.Add(text);

        (string Label, MessageBoxResult Result)[] options = buttons switch
        {
            MessageBoxButton.OKCancel => [("OK", MessageBoxResult.OK), ("Cancelar", MessageBoxResult.Cancel)],
            MessageBoxButton.YesNo => [("Sim", MessageBoxResult.Yes), ("Não", MessageBoxResult.No)],
            MessageBoxButton.YesNoCancel => [("Sim", MessageBoxResult.Yes), ("Não", MessageBoxResult.No), ("Cancelar", MessageBoxResult.Cancel)],
            _ => [("OK", MessageBoxResult.OK)]
        };
        var chosen = options.Any(o => o.Result == defaultResult) ? defaultResult : options[0].Result;
        // Fechar sem escolher equivale à opção de recusa, como na MessageBox.
        var dismiss = options.Select(o => o.Result).FirstOrDefault(r => r == MessageBoxResult.Cancel,
            options.Select(o => o.Result).FirstOrDefault(r => r == MessageBoxResult.No, options[0].Result));
        Result = dismiss;

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 24, 24, 22) };
        var list = new List<Button>();
        foreach (var (label, result) in options)
        {
            var button = new Button { Content = label, MinWidth = 88, IsDefault = result == chosen, IsCancel = result == dismiss };
            if (result == chosen) button.Style = (Style)FindResource("Primary");
            // O botão de recusa (IsCancel) já fecha a janela sozinho; os demais fecham aqui.
            button.Click += (_, _) => { Result = result; if (result != dismiss) DialogResult = true; };
            actions.Children.Add(button); list.Add(button);
        }
        ((Button)actions.Children[^1]).Margin = new Thickness(0);
        Buttons = list;
        Loaded += (_, _) => list.Single(b => b.IsDefault).Focus();

        var panel = new DockPanel();
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(actions); panel.Children.Add(body);
        Content = panel;
    }

    public static MessageBoxResult Show(Window? owner, string message, string title = "", MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var window = new MessageWindow(message, title, buttons, image, defaultResult);
        if (owner is { IsLoaded: true }) window.Owner = owner;
        else { window.WindowStartupLocation = WindowStartupLocation.CenterScreen; window.ShowInTaskbar = true; }
        window.ShowDialog();
        return window.Result;
    }
}
