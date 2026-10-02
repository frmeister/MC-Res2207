// ConnectTogether.App/Controls/DashedBorder.cs

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ConnectTogether.App.Controls
{
    /// <summary>
    /// Рамка с пунктиром (border: dashed) — у стандартного Border в WPF такой нет.
    /// Используется в пустых состояниях и в примере сообщения для друга.
    /// </summary>
    public sealed class DashedBorder : Decorator
    {
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            nameof(Stroke), typeof(Brush), typeof(DashedBorder),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
            nameof(StrokeThickness), typeof(double), typeof(DashedBorder),
            new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
            nameof(CornerRadius), typeof(double), typeof(DashedBorder),
            new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
            nameof(Padding), typeof(Thickness), typeof(DashedBorder),
            new FrameworkPropertyMetadata(new Thickness(), FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
            nameof(Background), typeof(Brush), typeof(DashedBorder),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public DashedBorder()
        {
            SetResourceReference(StrokeProperty, "Bd2");
        }

        public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
        public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
        public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
        public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
        public Brush? Background { get => (Brush?)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

        protected override Size MeasureOverride(Size constraint)
        {
            var p = Padding;
            double extraW = p.Left + p.Right, extraH = p.Top + p.Bottom;
            if (Child == null) return new Size(extraW, extraH);

            Child.Measure(new Size(Math.Max(0, constraint.Width - extraW), Math.Max(0, constraint.Height - extraH)));
            return new Size(Child.DesiredSize.Width + extraW, Child.DesiredSize.Height + extraH);
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            var p = Padding;
            Child?.Arrange(new Rect(p.Left, p.Top,
                Math.Max(0, arrangeSize.Width - p.Left - p.Right),
                Math.Max(0, arrangeSize.Height - p.Top - p.Bottom)));
            return arrangeSize;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double t = StrokeThickness;
            var rect = new Rect(t / 2, t / 2, Math.Max(0, ActualWidth - t), Math.Max(0, ActualHeight - t));
            var pen = new Pen(Stroke, t) { DashStyle = new DashStyle(new[] { 3.0, 2.0 }, 0) };
            dc.DrawRoundedRectangle(Background, pen, rect, CornerRadius, CornerRadius);
        }
    }
}
