// ConnectTogether.App/Controls/Icon.cs

using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ConnectTogether.App.Controls
{
    /// <summary>
    /// Контурная иконка из <see cref="IconGeometry"/>. Цвет берётся из унаследованного Foreground,
    /// как currentColor в макете: иконка внутри кнопки сама окрашивается в цвет её текста.
    /// Толщина линии задаётся в единицах сетки 24×24 и масштабируется вместе с иконкой.
    /// </summary>
    public sealed class Icon : FrameworkElement
    {
        public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
            nameof(Kind), typeof(string), typeof(Icon),
            new FrameworkPropertyMetadata("dot", FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
            nameof(StrokeWidth), typeof(double), typeof(Icon),
            new FrameworkPropertyMetadata(1.8, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
            typeof(Icon),
            new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

        public string? Kind
        {
            get => (string?)GetValue(KindProperty);
            set => SetValue(KindProperty, value);
        }

        public double StrokeWidth
        {
            get => (double)GetValue(StrokeWidthProperty);
            set => SetValue(StrokeWidthProperty, value);
        }

        public Brush Foreground
        {
            get => (Brush)GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 0) return;

            double scale = size / 24;
            var pen = new Pen(Foreground, StrokeWidth)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                DashCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };

            dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawGeometry(null, pen, IconGeometry.Get(Kind));
            dc.Pop();
            dc.Pop();
        }
    }
}
