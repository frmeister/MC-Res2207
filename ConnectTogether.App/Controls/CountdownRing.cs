// ConnectTogether.App/Controls/CountdownRing.cs

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ConnectTogether.App.Controls
{
    /// <summary>
    /// Кольцо обратного отсчёта: дорожка и дуга от верхней точки по часовой стрелке.
    /// Progress (0–1) — оставшаяся доля; при изменении дуга плавно доезжает за 1 с, как в макете.
    /// </summary>
    public sealed class CountdownRing : FrameworkElement
    {
        public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
            nameof(Progress), typeof(double), typeof(CountdownRing),
            new FrameworkPropertyMetadata(1.0, OnProgressChanged));

        private static readonly DependencyProperty DisplayedProgressProperty = DependencyProperty.Register(
            "DisplayedProgress", typeof(double), typeof(CountdownRing),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
            nameof(Thickness), typeof(double), typeof(CountdownRing),
            new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
            nameof(TrackBrush), typeof(Brush), typeof(CountdownRing),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ArcBrushProperty = DependencyProperty.Register(
            nameof(ArcBrush), typeof(Brush), typeof(CountdownRing),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public CountdownRing()
        {
            SetResourceReference(TrackBrushProperty, "Sf3");
            SetResourceReference(ArcBrushProperty, "Wn");
        }

        public double Progress
        {
            get => (double)GetValue(ProgressProperty);
            set => SetValue(ProgressProperty, value);
        }

        public double Thickness
        {
            get => (double)GetValue(ThicknessProperty);
            set => SetValue(ThicknessProperty, value);
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

        private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ring = (CountdownRing)d;
            double to = Math.Clamp((double)e.NewValue, 0, 1);
            // Сброс на полный круг — мгновенно, убывание — плавно
            var duration = to > (double)e.OldValue ? TimeSpan.Zero : TimeSpan.FromSeconds(1);
            ring.BeginAnimation(DisplayedProgressProperty, new DoubleAnimation(to, duration));
        }

        protected override void OnRender(DrawingContext dc)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 0) return;

            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            double r = (size - Thickness) / 2;
            dc.DrawEllipse(null, new Pen(TrackBrush, Thickness), center, r, r);

            double progress = (double)GetValue(DisplayedProgressProperty);
            var pen = new Pen(ArcBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (progress >= 0.999)
            {
                dc.DrawEllipse(null, pen, center, r, r);
                return;
            }
            if (progress <= 0.001) return;

            double sweep = progress * 360;
            var arc = new StreamGeometry();
            using (var ctx = arc.Open())
            {
                ctx.BeginFigure(Spinner.PointOnCircle(center, r, -90), false, false);
                ctx.ArcTo(Spinner.PointOnCircle(center, r, -90 + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }
            arc.Freeze();
            dc.DrawGeometry(null, pen, arc);
        }
    }
}
