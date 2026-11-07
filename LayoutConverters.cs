using System.Globalization;
using System.Windows.Data;

namespace Hush;

// Constrain variable-height rows to the current viewport, including after a resize.
// An unconstrained virtualized item can retain its earlier desired width.
public sealed class ReviewRowWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width ? Math.Max(100, width - (parameter is string inset ? double.Parse(inset, CultureInfo.InvariantCulture) : 14)) : 100.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
