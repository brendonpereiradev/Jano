using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using Vistora.Core;

namespace Vistora.Desktop;

/// <summary>
/// Tema claro ou escuro: troca o dicionário de cores em App.xaml e escurece a barra de título do Windows.
/// Tudo o que muda com o tema lê as cores por DynamicResource, então a troca vale na hora para as janelas abertas.
/// </summary>
public static class Theme
{
    private static readonly string Assembly = typeof(Theme).Assembly.GetName().Name!;
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static void Apply(AppTheme theme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var index = dictionaries.ToList().FindIndex(d => d.Contains("AppCanvasBackgroundBrush"));
        var name = theme == AppTheme.Dark ? "Dark" : "Light";
        dictionaries[index] = new ResourceDictionary { Source = new Uri($"/{Assembly};component/Themes/ThemeTokens.{name}.xaml", UriKind.Relative) };
        Current = theme;
        foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);
    }

    /// <summary>Faz cada janela nova seguir o tema na barra de título. Chamado uma vez, na inicialização.</summary>
    public static void FollowNewWindows() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => ApplyTitleBar((Window)sender)));

    // A barra de título continua sendo do Windows; no tema escuro ela é pedida escura (Windows 10 20H1 ou mais novo).
    private static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = Current == AppTheme.Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref dark, sizeof(int));
    }

    private const int UseImmersiveDarkMode = 20;
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

/// <summary>
/// Cores de estado ligadas ao tema. Um conversor devolveria o pincel do tema em que a tela foi desenhada;
/// estas propriedades guardam a chave do recurso (SetResourceReference) e acompanham a troca de tema.
/// </summary>
public static class Tone
{
    // Cor do texto de um StatusTone.
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.RegisterAttached("Foreground", typeof(StatusTone),
        typeof(Tone), new PropertyMetadata(StatusTone.Text, (d, e) => PaintText(d, (StatusTone)e.NewValue)));
    public static StatusTone GetForeground(DependencyObject element) => (StatusTone)element.GetValue(ForegroundProperty);
    public static void SetForeground(DependencyObject element, StatusTone value) => element.SetValue(ForegroundProperty, value);

    // Cor do texto de um RunState: concluída = sucesso, em execução = texto, interrompida = atenção, demais = erro.
    public static readonly DependencyProperty RunStateProperty = DependencyProperty.RegisterAttached("RunState", typeof(RunState),
        typeof(Tone), new PropertyMetadata(RunState.Running, (d, e) => PaintText(d, StatusTones.For((RunState)e.NewValue))));
    public static RunState GetRunState(DependencyObject element) => (RunState)element.GetValue(RunStateProperty);
    public static void SetRunState(DependencyObject element, RunState value) => element.SetValue(RunStateProperty, value);

    // Segmento n (1 a 4) do indicador de etapa: azul enquanto o chamado avança, verde quando fecha, trilho se ainda não chegou.
    public static readonly DependencyProperty StageProperty = DependencyProperty.RegisterAttached("Stage", typeof(FloorStage),
        typeof(Tone), new PropertyMetadata(FloorStage.Pending, (d, _) => PaintSegment(d)));
    public static FloorStage GetStage(DependencyObject element) => (FloorStage)element.GetValue(StageProperty);
    public static void SetStage(DependencyObject element, FloorStage value) => element.SetValue(StageProperty, value);

    public static readonly DependencyProperty SegmentProperty = DependencyProperty.RegisterAttached("Segment", typeof(int),
        typeof(Tone), new PropertyMetadata(0, (d, _) => PaintSegment(d)));
    public static int GetSegment(DependencyObject element) => (int)element.GetValue(SegmentProperty);
    public static void SetSegment(DependencyObject element, int value) => element.SetValue(SegmentProperty, value);

    public static string KeyFor(StatusTone tone) => tone switch
    {
        StatusTone.Muted => "AppTextSecondaryBrush",
        StatusTone.Success => "AppSuccessTextBrush",
        StatusTone.Attention => "AppAttentionTextBrush",
        StatusTone.Danger => "AppDangerTextBrush",
        _ => "AppTextPrimaryBrush"
    };

    private static void PaintText(DependencyObject element, StatusTone tone) =>
        ((FrameworkElement)element).SetResourceReference(TextElement.ForegroundProperty, KeyFor(tone));

    private static void PaintSegment(DependencyObject element)
    {
        var stage = GetStage(element);
        var key = (int)stage < GetSegment(element) ? "AppTrackBrush"
            : stage == FloorStage.Closed ? "AppSuccessBrush" : "AppBrandAccentBrush";
        ((FrameworkElement)element).SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, key);
    }
}
