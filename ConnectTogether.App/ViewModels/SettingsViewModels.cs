// ConnectTogether.App/ViewModels/SettingsViewModels.cs

using System.IO;
using System.Text;
using System.Windows.Input;
using ConnectTogether.App.Models;
using ConnectTogether.App.Mvvm;
using ConnectTogether.App.Services;

namespace ConnectTogether.App.ViewModels
{
    /// <summary>Интеграции (1l): установленные игры и как добавить свою.</summary>
    public sealed class IntegrationsViewModel : ObservableObject
    {
        public IntegrationsViewModel(ShellViewModel shell)
        {
            Items = shell.Services.Integrations.All;
            AddFromFileCommand = new RelayCommand(() =>
                shell.Toast("Установка интеграций из файла появится вместе с SDK", ToastKind.Info));
            GuideCommand = new RelayCommand(() => AppLinks.Open(AppLinks.IntegrationGuide));
        }

        public IReadOnlyList<GameIntegration> Items { get; }

        public ICommand AddFromFileCommand { get; }
        public ICommand GuideCommand { get; }
    }

    /// <summary>Настройки (1m): имя, тема, поддержка, порт приложения.</summary>
    public sealed class SettingsViewModel : ObservableObject
    {
        private const int MinPort = 1024;
        private readonly ShellViewModel _shell;
        private string _name;
        private string _port;
        private string? _portError;
        private bool _advancedOpen = true;

        public SettingsViewModel(ShellViewModel shell)
        {
            _shell = shell;
            var s = shell.Services.Settings.Current;
            _name = s.PlayerName;
            _port = s.AppPort.ToString();

            ResetPortCommand = new RelayCommand(() => Port = AppSettings.DefaultAppPort.ToString());
            OpenLogsCommand = new RelayCommand(() => AppLinks.OpenFolder(Path.Combine(AppContext.BaseDirectory, "logs")));
            CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics);
        }

        private SettingsStore Store => _shell.Services.Settings;

        public string Name
        {
            get => _name;
            set
            {
                if (!Set(ref _name, value)) return;
                string trimmed = value.Trim();
                if (trimmed.Length > 0) Store.Update(s => s.PlayerName = trimmed); // Пустое имя не сохраняем
            }
        }

        public bool IsDark
        {
            get => Store.Current.Theme == ThemeMode.Dark;
            set { if (value) SetTheme(ThemeMode.Dark); }
        }

        public bool IsLight
        {
            get => Store.Current.Theme == ThemeMode.Light;
            set { if (value) SetTheme(ThemeMode.Light); }
        }

        public bool IsSystem
        {
            get => Store.Current.Theme == ThemeMode.System;
            set { if (value) SetTheme(ThemeMode.System); }
        }

        public string Port
        {
            get => _port;
            set
            {
                if (!Set(ref _port, value)) return;
                if (int.TryParse(value, out int port) && port is >= MinPort and <= 65535)
                {
                    PortError = null;
                    Store.Update(s => s.AppPort = port);
                }
                else
                {
                    PortError = $"Порт — число от {MinPort} до 65535.";
                }
            }
        }

        public string? PortError
        {
            get => _portError;
            private set
            {
                if (Set(ref _portError, value)) OnPropertyChanged(nameof(HasPortError));
            }
        }

        public bool HasPortError => PortError != null;

        public string DefaultPortHint => $"UDP · по умолчанию {AppSettings.DefaultAppPort}";

        public bool AdvancedOpen
        {
            get => _advancedOpen;
            set => Set(ref _advancedOpen, value);
        }

        public ICommand ResetPortCommand { get; }
        public ICommand OpenLogsCommand { get; }
        public ICommand CopyDiagnosticsCommand { get; }

        private void SetTheme(ThemeMode mode)
        {
            Store.Update(s => s.Theme = mode);
            _shell.Services.Theme.Apply(mode);
            OnPropertyChanged(nameof(IsDark));
            OnPropertyChanged(nameof(IsLight));
            OnPropertyChanged(nameof(IsSystem));
        }

        /// <summary>
        /// Диагностика для поддержки: версия, система, состояние порта, интеграции.
        /// Без имени, адресов и содержимого логов — личных данных в ней нет.
        /// </summary>
        private void CopyDiagnostics()
        {
            var s = Store.Current;
            string room = _shell.ActiveRoom switch
            {
                HostRoomViewModel host => $"хост, {host.Game.Id}, игроков {host.Room.Players.Count}",
                PlayerRoomViewModel player => $"игрок, {player.Game.Id}, пинг {player.Room.PingMs} мс",
                _ => "нет",
            };

            var text = new StringBuilder()
                .AppendLine($"ConnectTogether {OnboardingViewModel.AppVersion}")
                .AppendLine($"Windows: {Environment.OSVersion.Version}, .NET {Environment.Version}")
                .AppendLine($"Порт приложения: UDP {s.AppPort} — {(PortInspector.IsUdpPortInUse(s.AppPort) ? "занят" : "свободен")}")
                .AppendLine($"Интеграции: {string.Join(", ", _shell.Services.Integrations.All.Select(i => $"{i.Id} {i.Version}"))}")
                .AppendLine($"Комната: {room}")
                .AppendLine($"Тема: {s.Theme}")
                .AppendLine($"Время: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}")
                .ToString();

            if (AppServices.CopyToClipboard(text))
                _shell.Toast("Диагностика скопирована");
        }
    }
}
