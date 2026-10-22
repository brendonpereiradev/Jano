using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Vistora.Desktop;

// Confirmação de exclusão com Cancelar como padrão, para que Enter ou Esc nunca apaguem dados.
public sealed class DangerConfirmWindow : Window
{
    public DangerConfirmWindow(string title, string message, string confirmLabel)
    {
        Title = title;
        Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0) };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, IsDefault = true };
        var clear = new Button { Content = confirmLabel, Background = (Brush)FindResource("Red"), Foreground = Brushes.White };
        clear.Click += (_, _) => DialogResult = true;
        actions.Children.Add(cancel); actions.Children.Add(clear); panel.Children.Add(actions);
        Content = panel;
    }
}
