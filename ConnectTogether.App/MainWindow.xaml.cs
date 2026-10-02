// ConnectTogether.App/MainWindow.xaml.cs

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ConnectTogether.App.Controls;
using ConnectTogether.App.ViewModels;

namespace ConnectTogether.App
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Icon = BrandIcon.Render(32);
            SourceInitialized += (_, _) => ApplyWindowFrame();
            StateChanged += (_, _) => UpdateMaximizedLayout();
            // Хост при выходе закрывает комнату — игроки сразу видят «комната закрыта», а не ждут таймаута
            Closing += (_, _) => (DataContext as ShellViewModel)?.ShutdownRooms();
            // Тема сменилась — DynamicResource подменил кисть рамки; перекрашиваем и системную рамку
            DependencyPropertyDescriptor.FromProperty(Border.BorderBrushProperty, typeof(Border))
                .AddValueChanged(Root, (_, _) => ApplyWindowFrame());
        }

        /// <summary>
        /// Развёрнутое окно с WindowChrome выходит за край экрана на толщину рамки — компенсируем отступом.
        /// </summary>
        private void UpdateMaximizedLayout()
        {
            if (WindowState == WindowState.Maximized)
            {
                double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                double frame = (GetSystemMetrics(SmCxSizeFrame) + GetSystemMetrics(SmCxPaddedBorder)) / dpi;
                Root.Margin = new Thickness(frame);
                Root.BorderThickness = new Thickness(0);
            }
            else
            {
                Root.Margin = new Thickness(0);
                Root.BorderThickness = new Thickness(1);
            }
        }

        /// <summary>
        /// Windows 11: скруглённые углы окна и системная рамка цвета разделителей темы (--bd).
        /// На Windows 10 вызовы просто не действуют.
        /// </summary>
        private void ApplyWindowFrame()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int round = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));

            if (TryFindResource("Bd") is SolidColorBrush border)
            {
                var c = border.Color;
                int colorRef = c.R | (c.G << 8) | (c.B << 16);
                DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref colorRef, sizeof(int));
            }
        }

        private const int SmCxSizeFrame = 32;
        private const int SmCxPaddedBorder = 92;
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwaBorderColor = 34;
        private const int DwmwcpRound = 2;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
