// ConnectTogether.App/Mvvm/ObservableObject.cs

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ConnectTogether.App.Mvvm
{
    /// <summary>
    /// Базовый класс моделей представления с INotifyPropertyChanged.
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>Записывает значение и уведомляет об изменении; возвращает false, если значение не изменилось.</summary>
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
