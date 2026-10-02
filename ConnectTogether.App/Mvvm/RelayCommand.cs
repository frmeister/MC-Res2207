// ConnectTogether.App/Mvvm/RelayCommand.cs

using System.Diagnostics;
using System.Windows.Input;

namespace ConnectTogether.App.Mvvm
{
    /// <summary>
    /// Команда из делегата. CanExecute пересчитывается вместе с остальными командами WPF
    /// (CommandManager.RequerySuggested) или вручную через <see cref="Refresh"/>.
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute == null ? null : _ => canExecute())
        {
        }

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => _execute(parameter);

        public static void Refresh() => CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Асинхронная команда: пока задача выполняется, повторный запуск запрещён.
    /// Исключения пишутся в лог, чтобы не терялись в async void.
    /// </summary>
    public sealed class AsyncCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool>? _canExecute;
        private bool _running;

        public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool IsRunning => _running;

        public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

        public async void Execute(object? parameter)
        {
            if (_running) return;
            _running = true;
            RelayCommand.Refresh();
            try
            {
                await _execute();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[AsyncCommand] Unhandled exception: {ex}");
            }
            finally
            {
                _running = false;
                RelayCommand.Refresh();
            }
        }
    }
}
