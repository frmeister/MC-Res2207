// ConnectTogether.App/Views/ShellView.xaml.cs

using System.Windows;
using System.Windows.Controls;
using ConnectTogether.App.Controls;

namespace ConnectTogether.App.Views
{
    public partial class ShellView : UserControl
    {
        public ShellView()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                var window = Window.GetWindow(this);
                if (window == null) return;
                window.StateChanged += (_, _) => UpdateMaximizeButton(window);
                UpdateMaximizeButton(window);
            };
        }

        private void UpdateMaximizeButton(Window window)
        {
            bool maximized = window.WindowState == WindowState.Maximized;
            Ui.SetIcon(MaximizeButton, maximized ? "restore" : "max");
            MaximizeButton.ToolTip = maximized ? "Свернуть в окно" : "Развернуть";
        }

        private void OnMinimize(object sender, RoutedEventArgs e) =>
            SystemCommands.MinimizeWindow(Window.GetWindow(this));

        private void OnMaximize(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window);
            else SystemCommands.MaximizeWindow(window);
        }

        private void OnClose(object sender, RoutedEventArgs e) =>
            SystemCommands.CloseWindow(Window.GetWindow(this));
    }
}
