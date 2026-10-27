using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Vistora.Desktop;

/// <summary>
/// Emblema pequeno da barra de título (o desenho de 24 a 32 px de Themes/Brand.xaml) em formas separadas.
/// Enquanto há trabalho em andamento, as metades acendem em sequência, de baixo para cima, como chamados abrindo
/// (azul) e fechando (menta). Fica parado quando o Windows está com as animações desligadas.
/// </summary>
public sealed class JanoEmblem : Viewbox
{
    public static readonly DependencyProperty IsAnimatingProperty = DependencyProperty.Register(nameof(IsAnimating), typeof(bool),
        typeof(JanoEmblem), new PropertyMetadata(false, (d, _) => ((JanoEmblem)d).UpdateAnimation()));
    public bool IsAnimating { get => (bool)GetValue(IsAnimatingProperty); set => SetValue(IsAnimatingProperty, value); }

    private const double Dim = 0.25, Cycle = 2.0;
    private readonly Storyboard story = new() { RepeatBehavior = RepeatBehavior.Forever };

    public JanoEmblem()
    {
        Width = 22; Height = 22; Focusable = false; IsHitTestVisible = false;
        var canvas = new Canvas { Width = 128, Height = 128 };
        var tile = new Rectangle { Width = 128, Height = 128, RadiusX = 28, RadiusY = 28 };
        tile.SetResourceReference(Shape.FillProperty, "JanoTileBrush");
        canvas.Children.Add(tile);
        // A moldura azul do emblema completo, mais grossa: sem ela, o ladrilho some sobre a barra escura.
        var frame = new Rectangle { Width = 122, Height = 122, RadiusX = 25, RadiusY = 25, StrokeThickness = 6 };
        Canvas.SetLeft(frame, 3); Canvas.SetTop(frame, 3);
        frame.SetResourceReference(Shape.StrokeProperty, "JanoFrameBrush");
        canvas.Children.Add(frame);
        canvas.Children.Add(Stroke("M36,100 V62 A28,28 0 0 1 92,62 V100", "JanoPortalBrush"));
        // Ordem em que acendem: abertura e fechamento do pavimento de baixo, depois os do pavimento de cima.
        string[] halves = ["M50,88 H56", "M72,88 H78", "M50,68 H56", "M72,68 H78"];
        for (var i = 0; i < halves.Length; i++)
        {
            var half = Stroke(halves[i], i % 2 == 0 ? "JanoOpenBrush" : "JanoCloseBrush");
            canvas.Children.Add(half);
            var start = 0.25 + i * 0.3;
            var light = new DoubleAnimationUsingKeyFrames();
            light.KeyFrames.Add(new DiscreteDoubleKeyFrame(Dim, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            light.KeyFrames.Add(new LinearDoubleKeyFrame(Dim, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start))));
            light.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start + 0.15))));
            light.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Cycle - 0.3))));
            light.KeyFrames.Add(new LinearDoubleKeyFrame(Dim, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Cycle))));
            Storyboard.SetTarget(light, half);
            Storyboard.SetTargetProperty(light, new PropertyPath(OpacityProperty));
            story.Children.Add(light);
        }
        Child = canvas;
        Unloaded += (_, _) => story.Stop(this);
        Loaded += (_, _) => UpdateAnimation();
    }

    private static Path Stroke(string data, string brush)
    {
        var path = new Path { Data = Geometry.Parse(data), StrokeThickness = 10, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        path.SetResourceReference(Shape.StrokeProperty, brush);
        return path;
    }

    private void UpdateAnimation()
    {
        if (IsAnimating && SystemParameters.ClientAreaAnimation && IsLoaded) story.Begin(this, true);
        else story.Stop(this);
    }
}
