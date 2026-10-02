// ConnectTogether.App/App.xaml.cs

using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using ConnectTogether.App.Diagnostics;
using ConnectTogether.App.Services;
using ConnectTogether.App.ViewModels;
using MC_Ref2207_NetSocketLib;

namespace ConnectTogether.App
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
#if DEBUG
            // Ошибки привязок XAML (опечатка в имени свойства и т. п.) — в тот же лог, иначе они видны только в отладчике.
            // Refresh сбрасывает Trace.Listeners, поэтому он идёт до запуска файлового лога.
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingErrorListener());
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
#endif
            DebugFileLogger.Start();
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Trace.WriteLine($"[App] Unobserved task exception: {args.Exception}");
                args.SetObserved();
            };

            // Инструменты разработчика: снимки экранов и экспорт иконки (см. Diagnostics/DevTools.cs)
            if (DevTools.TryRun(e.Args))
            {
                Shutdown();
                return;
            }

            // --screen <номер> открывает экран из макета с демо-данными (Diagnostics/ScreenPreview.cs)
            var shell = ScreenPreview.TryCreateShell(e.Args) ?? CreateShell();
            var window = new MainWindow { DataContext = shell };
            MainWindow = window;
            window.Show();
        }

        private static ShellViewModel CreateShell()
        {
            var services = AppServices.CreateDefault();
            services.Theme.Apply(services.Settings.Current.Theme);
            var shell = new ShellViewModel(services);
            shell.Start();
            return shell;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            DebugFileLogger.Stop();
            base.OnExit(e);
        }

        private sealed class BindingErrorListener : TraceListener
        {
            public override void Write(string? message) { }

            public override void WriteLine(string? message) => Trace.WriteLine($"[Binding] {message}");
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // Пишем в лог и продолжаем работу: одна сломанная кнопка не должна закрывать комнату с друзьями
            Trace.WriteLine($"[App] Unhandled UI exception: {e.Exception}");
            e.Handled = true;
        }
    }
}
