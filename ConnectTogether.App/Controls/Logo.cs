// ConnectTogether.App/Controls/Logo.cs

using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ConnectTogether.App.Controls
{
    /// <summary>
    /// Знак ConnectTogether: две точки (хост и игрок) и плавная линия между ними.
    /// Цветной вариант: линия и первая точка акцентные, вторая — цвета текста.
    /// Одноцветный (IsMono) рисуется унаследованным Foreground — для иконки на акцентной плашке.
    /// </summary>
    public sealed class Logo : FrameworkElement
    {
        private static readonly Geometry Arc = CreateArc();

        public static readonly DependencyProperty IsMonoProperty = DependencyProperty.Register(
            nameof(IsMono), typeof(bool), typeof(Logo),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
            nameof(AccentBrush), typeof(Brush), typeof(Logo),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty DotBrushProperty = DependencyProperty.Register(
            nameof(DotBrush), typeof(Brush), typeof(Logo),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
            typeof(Logo),
            new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

        public Logo()
        {
            SetResourceReference(AccentBrushProperty, "Ac");
            SetResourceReference(DotBrushProperty, "Tx");
        }

        public bool IsMono
        {
            get => (bool)GetValue(IsMonoProperty);
            set => SetValue(IsMonoProperty, value);
        }

        public Brush? AccentBrush
        {
            get => (Brush?)GetValue(AccentBrushProperty);
            set => SetValue(AccentBrushProperty, value);
        }

        public Brush? DotBrush
        {
            get => (Brush?)GetValue(DotBrushProperty);
            set => SetValue(DotBrushProperty, value);
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

            var accent = IsMono ? Foreground : AccentBrush ?? Foreground;
            var dot = IsMono ? Foreground : DotBrush ?? Foreground;
            var pen = new Pen(accent, 2.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

            double scale = size / 24;
            dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawGeometry(null, pen, Arc);
            dc.DrawEllipse(accent, null, new Point(5, 6.5), 3.1, 3.1);
            dc.DrawEllipse(dot, null, new Point(19, 17.5), 3.1, 3.1);
            dc.Pop();
            dc.Pop();
        }

        private static Geometry CreateArc()
        {
            var geometry = Geometry.Parse("M5 6.5C13 6.5 11 17.5 19 17.5");
            geometry.Freeze();
            return geometry;
        }
    }
}
