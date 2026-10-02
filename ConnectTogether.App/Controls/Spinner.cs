// ConnectTogether.App/Controls/Spinner.cs

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ConnectTogether.App.Controls
{
    /// <summary>
    /// Индикатор «идёт работа»: бледное кольцо и акцентная четверть, оборот за 0,9 с.
    /// </summary>
    public sealed class Spinner : FrameworkElement
    {
        private const double Thickness = 2.5;
        private readonly RotateTransform _rotation = new();

        public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
            nameof(TrackBrush), typeof(Brush), typeof(Spinner),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ArcBrushProperty = DependencyProperty.Register(
            nameof(ArcBrush), typeof(Brush), typeof(Spinner),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public Spinner()
        {
            SetResourceReference(TrackBrushProperty, "AcSoft");
            SetResourceReference(ArcBrushProperty, "Ac");
            RenderTransform = _rotation;
            RenderTransformOrigin = new Point(0.5, 0.5);
            Loaded += (_, _) => _rotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
            Unloaded += (_, _) => _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        public Brush? TrackBrush
        {
            get => (Brush?)GetValue(TrackBrushProperty);
            set => SetValue(TrackBrushProperty, value);
        }

        public Brush? ArcBrush
        {
            get => (Brush?)GetValue(ArcBrushProperty);
            set => SetValue(ArcBrushProperty, value);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 0) return;

            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            double r = (size - Thickness) / 2;
            dc.DrawEllipse(null, new Pen(TrackBrush, Thickness), center, r, r);

            // Четверть сверху: от −135° до −45° (ось Y экрана направлена вниз)
            var arc = new StreamGeometry();
            using (var ctx = arc.Open())
            {
                ctx.BeginFigure(PointOnCircle(center, r, -135), false, false);
                ctx.ArcTo(PointOnCircle(center, r, -45), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            }
            arc.Freeze();
            dc.DrawGeometry(null, new Pen(ArcBrush, Thickness), arc);
        }

        internal static Point PointOnCircle(Point center, double r, double degrees)
        {
            double rad = degrees * Math.PI / 180;
            return new Point(center.X + r * Math.Cos(rad), center.Y + r * Math.Sin(rad));
        }
    }
}
