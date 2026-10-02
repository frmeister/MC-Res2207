// ConnectTogether.App/Controls/SpacedText.cs

using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ConnectTogether.App.Controls
{
    /// <summary>
    /// Однострочный текст с межбуквенным интервалом (letter-spacing из макета), которого нет у TextBlock в WPF.
    /// Нужен для крупного кода приглашения. Символы из DimChars (по умолчанию дефис) рисуются кистью DimBrush.
    /// </summary>
    public sealed class SpacedText : FrameworkElement
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(SpacedText),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
            nameof(Spacing), typeof(double), typeof(SpacedText),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LineHeightProperty = DependencyProperty.Register(
            nameof(LineHeight), typeof(double), typeof(SpacedText),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty DimCharsProperty = DependencyProperty.Register(
            nameof(DimChars), typeof(string), typeof(SpacedText),
            new FrameworkPropertyMetadata("-", FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty DimBrushProperty = DependencyProperty.Register(
            nameof(DimBrush), typeof(Brush), typeof(SpacedText),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(SpacedText),
            new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(SpacedText),
            new FrameworkPropertyMetadata(SystemFonts.MessageFontSize, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(SpacedText),
            new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(SpacedText),
            new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

        public SpacedText()
        {
            SetResourceReference(DimBrushProperty, "Tx3");
        }

        public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
        public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
        public double LineHeight { get => (double)GetValue(LineHeightProperty); set => SetValue(LineHeightProperty, value); }
        public string DimChars { get => (string)GetValue(DimCharsProperty); set => SetValue(DimCharsProperty, value); }
        public Brush? DimBrush { get => (Brush?)GetValue(DimBrushProperty); set => SetValue(DimBrushProperty, value); }
        public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
        public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
        public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
        public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

        private FormattedText Format(char c, Brush brush) => new(
            c.ToString(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal),
            FontSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = 0, height = 0;
            foreach (char c in Text ?? "")
            {
                var ft = Format(c, Foreground);
                width += ft.WidthIncludingTrailingWhitespace + Spacing;
                height = Math.Max(height, ft.Height);
            }
            if (!double.IsNaN(LineHeight)) height = LineHeight;
            return new Size(width, height);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double x = 0;
            foreach (char c in Text ?? "")
            {
                var brush = DimChars.Contains(c) ? DimBrush ?? Foreground : Foreground;
                var ft = Format(c, brush);
                dc.DrawText(ft, new Point(x, (ActualHeight - ft.Height) / 2));
                x += ft.WidthIncludingTrailingWhitespace + Spacing;
            }
        }
    }
}
