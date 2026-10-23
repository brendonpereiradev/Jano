using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Vistora.Core;

namespace Vistora.Desktop;

/// <summary>Cor do texto de um RunState: concluída = teal, em execução = ink, interrompida = âmbar, demais = vermelho.</summary>
public sealed class RunStateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => For(value as RunState? ?? RunState.Failed);
    public static Brush For(RunState state) => StatusToneBrushConverter.For(StatusTones.For(state));
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Pincel de um StatusTone: o recurso de App.xaml com o mesmo nome.</summary>
public sealed class StatusToneBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => For(value as StatusTone? ?? StatusTone.Ink);
    public static Brush For(StatusTone tone) => (Brush)Application.Current.Resources[tone.ToString()];
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Segmento n (1 a 4) do indicador de etapa: teal quando o pavimento já alcançou a etapa.</summary>
public sealed class StageSegmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is FloorStage stage && (int)stage >= int.Parse((string)parameter, CultureInfo.InvariantCulture)
            ? Application.Current.Resources["Teal"] : Application.Current.Resources["Line"];
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Path.GetFileName(value as string ?? "");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
