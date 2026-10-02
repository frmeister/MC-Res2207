// ConnectTogether.App/Services/ToastService.cs

using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using ConnectTogether.App.Mvvm;

namespace ConnectTogether.App.Services
{
    public enum ToastKind
    {
        Success,
        Info,
        Warning,
    }

    /// <summary>Уведомление снизу справа.</summary>
    public sealed class Toast
    {
        public required string Text { get; init; }
        public ToastKind Kind { get; init; }
        public string? ActionLabel { get; init; }
        public ICommand? ActionCommand { get; init; }
        public required ICommand CloseCommand { get; init; }

        public string Icon => Kind switch
        {
            ToastKind.Success => "checkCircle",
            ToastKind.Warning => "alert",
            _ => "userPlus",
        };
    }

    /// <summary>Показывает тосты; каждый исчезает через 4 с или по крестику.</summary>
    public sealed class ToastService
    {
        private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(4);

        public ObservableCollection<Toast> Items { get; } = new();

        public void Show(string text, ToastKind kind = ToastKind.Success, string? actionLabel = null, Action? action = null)
        {
            Toast? toast = null;
            var timer = new DispatcherTimer { Interval = Lifetime };
            void Close()
            {
                timer.Stop();
                if (toast != null) Items.Remove(toast);
            }

            toast = new Toast
            {
                Text = text,
                Kind = kind,
                ActionLabel = actionLabel,
                ActionCommand = action == null ? null : new RelayCommand(() => { action(); Close(); }),
                CloseCommand = new RelayCommand(Close),
            };
            timer.Tick += (_, _) => Close();

            // Одинаковый тост не дублируем — повторное «Скопировано» просто продлевает показ
            var same = Items.FirstOrDefault(t => t.Text == text);
            if (same != null) Items.Remove(same);

            Items.Add(toast);
            while (Items.Count > 3) Items.RemoveAt(0);
            timer.Start();
        }
    }
}
