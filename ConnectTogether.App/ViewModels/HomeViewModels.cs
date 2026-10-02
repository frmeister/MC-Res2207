// ConnectTogether.App/ViewModels/HomeViewModels.cs

using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Input;
using ConnectTogether.App.Models;
using ConnectTogether.App.Mvvm;
using ConnectTogether.App.Services;

namespace ConnectTogether.App.ViewModels
{
    /// <summary>Первый запуск (1a): имя в комнате.</summary>
    public sealed class OnboardingViewModel : ObservableObject
    {
        private const int MaxNameLength = 24;
        private readonly ShellViewModel _shell;
        private string _name;

        public OnboardingViewModel(ShellViewModel shell)
        {
            _shell = shell;
            _name = shell.Services.PlayerName;
            ContinueCommand = new RelayCommand(Continue, () => !string.IsNullOrWhiteSpace(Name));
        }

        public string Name
        {
            get => _name;
            set => Set(ref _name, value.Length > MaxNameLength ? value[..MaxNameLength] : value);
        }

        public ICommand ContinueCommand { get; }

        public string VersionText => $"Открытый проект · версия {AppVersion}";

        public static string AppVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

        private void Continue()
        {
            string name = Name.Trim();
            _shell.Services.Settings.Update(s => s.PlayerName = name);
            _shell.GoHome();
        }
    }

    /// <summary>Главный экран (1b, 3h): два больших действия и недавние комнаты.</summary>
    public sealed class HomeViewModel : ObservableObject
    {
        private readonly ShellViewModel _shell;

        public HomeViewModel(ShellViewModel shell)
        {
            _shell = shell;
            CreateRoomCommand = new RelayCommand(() => shell.StartCreateRoom());
            JoinCommand = new RelayCommand(() => shell.StartJoin());
            ClearCommand = new RelayCommand(Clear);
            Reload();
        }

        public string Greeting => $"Привет, {_shell.Services.PlayerName}";

        public ObservableCollection<RecentRoomItem> RecentRooms { get; } = new();

        public bool HasRooms => RecentRooms.Count > 0;

        public ICommand CreateRoomCommand { get; }
        public ICommand JoinCommand { get; }
        public ICommand ClearCommand { get; }

        private void Clear()
        {
            _shell.Services.Settings.Update(s => s.RecentRooms.Clear());
            Reload();
        }

        private void Reload()
        {
            RecentRooms.Clear();
            var now = DateTime.Now;
            foreach (var room in _shell.Services.Settings.Current.RecentRooms)
            {
                var game = _shell.Services.Integrations.Find(room.IntegrationId);
                if (game == null) continue; // Интеграцию удалили — комнату не показываем

                string when = RussianText.RelativeTime(room.Time, now);
                RecentRooms.Add(room.IsHost
                    ? new RecentRoomItem(
                        game.Icon,
                        $"{game.Name} · ваша комната",
                        $"Вы хост · {room.PlayerCount} {RussianText.Plural(room.PlayerCount, "игрок", "игрока", "игроков")} · {when}",
                        "Создать снова",
                        new RelayCommand(() => _shell.StartCreateRoom(game.Id)))
                    : new RecentRoomItem(
                        game.Icon,
                        $"{game.Name} · комната {RussianText.Genitive(room.HostName)}",
                        $"Адрес {room.Address} · {when}",
                        "Подключиться",
                        new RelayCommand(() => _shell.StartJoin(room.Address))));
            }
            OnPropertyChanged(nameof(HasRooms));
        }
    }

    public sealed record RecentRoomItem(string Icon, string Title, string Meta, string ActionLabel, ICommand ActionCommand);
}
