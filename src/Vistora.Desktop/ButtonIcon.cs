using System.Windows;

namespace Vistora.Desktop;

/// <summary>
/// Ícone da Segoe MDL2 Assets à esquerda do texto de um botão. Fica fora do Content, que continua sendo o texto
/// (os testes e os comandos leem o Content). O modelo de Button em Themes/Controls.xaml mostra o ícone.
/// </summary>
public static class ButtonIcon
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached("Glyph", typeof(string), typeof(ButtonIcon));
    public static string? GetGlyph(DependencyObject element) => (string?)element.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject element, string? value) => element.SetValue(GlyphProperty, value);
}
