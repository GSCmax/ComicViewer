using System.Globalization;
using System.Windows.Data;

namespace ComicViewer;

public sealed class PageHeightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [double ratio, double width] ? Math.Max(1, ratio * width) : 1d;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
