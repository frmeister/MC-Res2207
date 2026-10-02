// ConnectTogether.App/Controls/Ui.cs

using System.Windows;

namespace ConnectTogether.App.Controls
{
    /// <summary>
    /// Присоединённые свойства для стилей: радиус, иконки в кнопках, плейсхолдер поля, активная вкладка.
    /// Благодаря им один шаблон кнопки покрывает все варианты из библиотеки компонентов.
    /// </summary>
    public static class Ui
    {
        public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
            "CornerRadius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(9)));

        public static CornerRadius GetCornerRadius(DependencyObject d) => (CornerRadius)d.GetValue(CornerRadiusProperty);
        public static void SetCornerRadius(DependencyObject d, CornerRadius value) => d.SetValue(CornerRadiusProperty, value);

        /// <summary>Иконка перед текстом кнопки.</summary>
        public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
            "Icon", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));

        public static string? GetIcon(DependencyObject d) => (string?)d.GetValue(IconProperty);
        public static void SetIcon(DependencyObject d, string? value) => d.SetValue(IconProperty, value);

        /// <summary>Иконка после текста кнопки (стрелка «Дальше», «внешняя ссылка»).</summary>
        public static readonly DependencyProperty TrailingIconProperty = DependencyProperty.RegisterAttached(
            "TrailingIcon", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));

        public static string? GetTrailingIcon(DependencyObject d) => (string?)d.GetValue(TrailingIconProperty);
        public static void SetTrailingIcon(DependencyObject d, string? value) => d.SetValue(TrailingIconProperty, value);

        public static readonly DependencyProperty IconSizeProperty = DependencyProperty.RegisterAttached(
            "IconSize", typeof(double), typeof(Ui), new FrameworkPropertyMetadata(16.0));

        public static double GetIconSize(DependencyObject d) => (double)d.GetValue(IconSizeProperty);
        public static void SetIconSize(DependencyObject d, double value) => d.SetValue(IconSizeProperty, value);

        public static readonly DependencyProperty IconStrokeProperty = DependencyProperty.RegisterAttached(
            "IconStroke", typeof(double), typeof(Ui), new FrameworkPropertyMetadata(1.8));

        public static double GetIconStroke(DependencyObject d) => (double)d.GetValue(IconStrokeProperty);
        public static void SetIconStroke(DependencyObject d, double value) => d.SetValue(IconStrokeProperty, value);

        /// <summary>Расстояние между иконкой и текстом (gap в макете).</summary>
        public static readonly DependencyProperty IconGapProperty = DependencyProperty.RegisterAttached(
            "IconGap", typeof(double), typeof(Ui), new FrameworkPropertyMetadata(8.0));

        public static double GetIconGap(DependencyObject d) => (double)d.GetValue(IconGapProperty);
        public static void SetIconGap(DependencyObject d, double value) => d.SetValue(IconGapProperty, value);

        public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
            "Placeholder", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));

        public static string? GetPlaceholder(DependencyObject d) => (string?)d.GetValue(PlaceholderProperty);
        public static void SetPlaceholder(DependencyObject d, string? value) => d.SetValue(PlaceholderProperty, value);

        /// <summary>Активная вкладка в заголовке окна.</summary>
        public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
            "IsActive", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));

        public static bool GetIsActive(DependencyObject d) => (bool)d.GetValue(IsActiveProperty);
        public static void SetIsActive(DependencyObject d, bool value) => d.SetValue(IsActiveProperty, value);

        /// <summary>Поле с ошибкой: красная граница вместо акцентной.</summary>
        public static readonly DependencyProperty HasErrorProperty = DependencyProperty.RegisterAttached(
            "HasError", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));

        public static bool GetHasError(DependencyObject d) => (bool)d.GetValue(HasErrorProperty);
        public static void SetHasError(DependencyObject d, bool value) => d.SetValue(HasErrorProperty, value);
    }
}
