using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace Vistora.Desktop;

// As cores de estado não usam conversores: ficam em Tone (Theme.cs), para acompanhar a troca de tema.

public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Path.GetFileName(value as string ?? "");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
