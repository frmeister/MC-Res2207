// ConnectTogether.App/Diagnostics/DevTools.cs

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ConnectTogether.App.Controls;
using ConnectTogether.App.ViewModels;
using ConnectTogether.App.Views;

namespace ConnectTogether.App.Diagnostics
{
    /// <summary>
    /// Инструменты разработчика, запускаются без окна:
    ///   --snapshot &lt;папка&gt; [--theme light] [--only 1b,1i] — PNG каждого экрана 1100×720 для сверки с макетом;
    ///   --export-icon &lt;файл.ico&gt; — иконка приложения 16–256 px из знака (Assets/AppIcon.ico).
    /// </summary>
    public static class DevTools
    {
        private const int WindowWidth = 1100;
        private const int WindowHeight = 720;

        public static bool TryRun(string[] args)
        {
            if (ArgValue(args, "--export-icon") is string icon)
            {
                ExportIcon(icon);
                return true;
            }
            if (ArgValue(args, "--snapshot") is string dir)
            {
                var only = ArgValue(args, "--only")?.Split(',');
                Snapshot(dir, ArgValue(args, "--theme"), only);
                return true;
            }
            return false;
        }

        public static string? ArgValue(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static void Snapshot(string dir, string? theme, string[]? only)
        {
            Directory.CreateDirectory(dir);
            foreach (var id in ScreenPreview.Ids.Where(i => only == null || only.Contains(i)))
            {
                var shell = new ShellViewModel(ScreenPreview.CreateServices(theme));
                ScreenPreview.Open(shell, id);

                var root = new Border
                {
                    Width = WindowWidth,
                    Height = WindowHeight,
                    BorderThickness = new Thickness(1),
                    UseLayoutRounding = true,
                    Child = new ShellView { DataContext = shell },
                };
                root.SetResourceReference(Border.BackgroundProperty, "Bg");
                root.SetResourceReference(Border.BorderBrushProperty, "Bd");
                root.SetResourceReference(TextElement.ForegroundProperty, "Tx");
                root.SetResourceReference(TextElement.FontFamilyProperty, "FontUi");
                TextElement.SetFontSize(root, 14);

                Layout(root);
                // Шаблоны и привязки частично применяются отложенно — даём диспетчеру отработать и меряем ещё раз
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Layout(root);

                var bitmap = new RenderTargetBitmap(WindowWidth, WindowHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                SavePng(bitmap, Path.Combine(dir, $"{id}{(theme == "light" ? "-light" : "")}.png"));
                (shell.Current as INavigationAware)?.OnNavigatedFrom();
            }
        }

        private static void Layout(FrameworkElement root)
        {
            root.Measure(new Size(WindowWidth, WindowHeight));
            root.Arrange(new Rect(0, 0, WindowWidth, WindowHeight));
            root.UpdateLayout();
        }

        private static void SavePng(BitmapSource bitmap, string path)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path);
            encoder.Save(file);
        }

        /// <summary>ICO с PNG-кадрами 16, 24, 32, 48 и 256 px (так хранят иконки начиная с Windows Vista).</summary>
        private static void ExportIcon(string path)
        {
            int[] sizes = { 16, 24, 32, 48, 256 };
            var frames = sizes.Select(size =>
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BrandIcon.Render(size)));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                return (size, data: ms.ToArray());
            }).ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var file = File.Create(path);
            using var w = new BinaryWriter(file);
            w.Write((short)0); // reserved
            w.Write((short)1); // тип: иконка
            w.Write((short)frames.Count);

            int offset = 6 + 16 * frames.Count;
            foreach (var (size, data) in frames)
            {
                w.Write((byte)(size >= 256 ? 0 : size)); // 0 означает 256
                w.Write((byte)(size >= 256 ? 0 : size));
                w.Write((byte)0); // палитра не используется
                w.Write((byte)0);
                w.Write((short)1); // плоскости
                w.Write((short)32); // бит на пиксель
                w.Write(data.Length);
                w.Write(offset);
                offset += data.Length;
            }
            foreach (var (_, data) in frames) w.Write(data);
        }
    }
}
