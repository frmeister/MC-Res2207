// ConnectTogether.App/Controls/PingBars.cs

using System.Windows;
using System.Windows.Media;

namespace ConnectTogether.App.Controls
{
    /// <summary>
    /// Индикатор качества связи: три деления 4 px через 2 px, высоты 6/10/14 при высоте 14.
    /// Level — сколько делений закрашено (0–3), остальные цвета границы полей.
    /// </summary>
    public sealed class PingBars : FrameworkElement
    {
        private const double BarWidth = 4;
        private const double Gap = 2;

        public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
            nameof(Level), typeof(int), typeof(PingBars),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ActiveBrushProperty = DependencyProperty.Register(
            nameof(ActiveBrush), typeof(Brush), typeof(PingBars),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty InactiveBrushProperty = DependencyProperty.Register(
            nameof(InactiveBrush), typeof(Brush), typeof(PingBars),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public PingBars()
        {
            SetResourceReference(InactiveBrushProperty, "Bd2");
        }

        public int Level
        {
            get => (int)GetValue(LevelProperty);
            set => SetValue(LevelProperty, value);
        }

        public Brush? ActiveBrush
        {
            get => (Brush?)GetValue(ActiveBrushProperty);
            set => SetValue(ActiveBrushProperty, value);
        }

        public Brush? InactiveBrush
        {
            get => (Brush?)GetValue(InactiveBrushProperty);
            set => SetValue(InactiveBrushProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double height = double.IsNaN(Height) ? 14 : Height;
            return new Size(BarWidth * 3 + Gap * 2, height);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double h = ActualHeight;
            double[] heights = { h * 6 / 14, h * 10 / 14, h };
            for (int i = 0; i < 3; i++)
            {
                var brush = i < Level ? ActiveBrush : InactiveBrush;
                var rect = new Rect(i * (BarWidth + Gap), h - heights[i], BarWidth, heights[i]);
                dc.DrawRoundedRectangle(brush, null, rect, 1, 1);
            }
        }
    }
}
