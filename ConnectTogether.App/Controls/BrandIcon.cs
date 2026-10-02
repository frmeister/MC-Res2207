// ConnectTogether.App/Controls/BrandIcon.cs

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ConnectTogether.App.Controls
{
    /// <summary>
    /// Иконка приложения: одноцветный знак на акцентной плашке (CT Library → «Логотип и иконка»).
    /// Цвета фиксированные, не зависят от темы.
    /// </summary>
    public static class BrandIcon
    {
        private static readonly Color Plate = Color.FromRgb(0x8E, 0x95, 0xFF);
        private static readonly Color Mark = Color.FromRgb(0x10, 0x12, 0x3A);

        public static FrameworkElement CreateVisual(double size)
        {
            // Радиус плашки и размер знака из макета для 16/32/48 и пропорции крупной иконки (144 → 32 и 88)
            (double radius, double glyph) = size switch
            {
                <= 16 => (4.0, 12.0),
                <= 24 => (6.0, 16.0),
                <= 32 => (8.0, 21.0),
                <= 48 => (11.0, 30.0),
                _ => (size * 32 / 144, size * 88 / 144),
            };

            var plate = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(radius),
                Background = new SolidColorBrush(Plate),
                Child = new Logo
                {
                    IsMono = true,
                    Width = glyph,
                    Height = glyph,
                    Foreground = new SolidColorBrush(Mark),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            plate.Measure(new Size(size, size));
            plate.Arrange(new Rect(0, 0, size, size));
            plate.UpdateLayout();
            return plate;
        }

        public static BitmapSource Render(int px)
        {
            var bitmap = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(CreateVisual(px));
            bitmap.Freeze();
            return bitmap;
        }
    }
}
