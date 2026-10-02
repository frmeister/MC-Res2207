// ConnectTogether.App/Controls/Converters.cs

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ConnectTogether.App.Controls
{
    /// <summary>true → Visible, false → Collapsed; с параметром "invert" наоборот.</summary>
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool flag = value is true;
            if (parameter as string == "invert") flag = !flag;
            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// null или пустая строка → Collapsed, иначе Visible.
    /// С RelativeSource PreviousData прячет разделитель над первой строкой списка.
    /// </summary>
    public sealed class NullToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value == null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Число → отступ только с одной стороны: параметр "right" или "left".</summary>
    public sealed class GapToThicknessConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double gap = value is double d ? d : 0;
            return parameter as string == "left" ? new Thickness(gap, 0, 0, 0) : new Thickness(0, 0, gap, 0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Увеличивает радиус скругления на параметр — для кольца фокуса снаружи элемента.</summary>
    public sealed class CornerRadiusPlusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var r = value is CornerRadius cr ? cr : new CornerRadius();
            double add = double.Parse((string)parameter, CultureInfo.InvariantCulture);
            return new CornerRadius(r.TopLeft + add, r.TopRight + add, r.BottomRight + add, r.BottomLeft + add);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Доля 0–1 → ширина столбца сетки в звёздочках: заполненная часть полосы прогресса,
    /// с параметром "rest" — оставшаяся.
    /// </summary>
    public sealed class ProgressToStarConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double p = Math.Clamp(value is double d ? d : 0, 0, 1);
            return new GridLength(parameter as string == "rest" ? 1 - p : p, GridUnitType.Star);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    public sealed class InvertBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    }
}
