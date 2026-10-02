// ConnectTogether.App/ViewModels/StateViewModel.cs

using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using ConnectTogether.App.Models;
using ConnectTogether.App.Mvvm;
using ConnectTogether.App.Services;

namespace ConnectTogether.App.ViewModels
{
    public enum StateTone
    {
        Warning,
        Error,
    }

    /// <summary>Карточка-вариант «что можно сделать» на экране ошибки связи.</summary>
    public sealed record StateOption(string Icon, string Title, string Description, string? Tag);

    /// <summary>
    /// Экран состояния или ошибки (CT States): что случилось, что делать дальше, технические детали — в «Подробнее».
    /// Фабричные методы собирают конкретные состояния 3a–3g.
    /// </summary>
    public class StateViewModel : ObservableObject
    {
        private bool _detailsOpen;
        private string _primaryLabel = "";

        public StateTone Tone { get; init; }
        public string Icon { get; init; } = "alert";
        public string Title { get; init; } = "";
        public string Description { get; init; } = "";
        public IReadOnlyList<NumberedItem> Tips { get; init; } = Array.Empty<NumberedItem>();
        public IReadOnlyList<StateOption> Options { get; init; } = Array.Empty<StateOption>();
        public virtual IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();
        public double ContentWidth { get; init; } = 600;

        public bool HasTips => Tips.Count > 0;
        public bool HasOptions => Options.Count > 0;

        /// <summary>Свой внешний адрес, который игрок отправляет хосту для «Впустить по адресу».</summary>
        public string? MyAddress { get; init; }

        public ICommand? CopyMyAddressCommand { get; init; }

        // Обратный отсчёт переподключения есть только у «Соединение потеряно»
        public virtual bool IsCountdown => false;
        public virtual int Seconds => 0;
        public virtual double Progress => 0;
        public virtual string CountdownTitle => "";
        public virtual string AttemptText => "";

        public string PrimaryLabel
        {
            get => _primaryLabel;
            set => Set(ref _primaryLabel, value);
        }

        public ICommand? PrimaryCommand { get; set; }
        public string SecondaryLabel { get; init; } = "";
        public ICommand? SecondaryCommand { get; set; }

        public bool DetailsOpen
        {
            get => _detailsOpen;
            set => Set(ref _detailsOpen, value);
        }

        private static IReadOnlyList<NumberedItem> Numbered(IEnumerable<string> tips) =>
            tips.Select((t, i) => new NumberedItem(i + 1, t)).ToList();

        /// <summary>
        /// 3a. Хост не ответил. Без сервера-посредника не отличить «комната закрыта» от «роутер хоста не пропускает
        /// входящие подключения», поэтому показываем, что проверить и что может сделать хост.
        /// </summary>
        public static StateViewModel NoAnswer(ShellViewModel shell, string address, IReadOnlyList<string> details, string? myAddress = null)
        {
            return new StateViewModel
            {
                Tone = StateTone.Warning,
                Icon = "clock",
                ContentWidth = 760,
                Title = "Друг не отвечает",
                Description = $"По адресу {address} никто не отозвался. Либо комната закрыта, либо роутер или брандмауэр друга " +
                              "не пропускает входящие подключения — тогда он может впустить вас сам.",
                Options = new[]
                {
                    new StateOption("search", "Проверьте адрес", "Сверьте его с экраном друга «Комната создана»: комната работает, пока у него открыт ConnectTogether.", null),
                    new StateOption("userPlus", "Впустить по адресу",
                        myAddress != null
                            ? "Отправьте другу ваш адрес ниже. Он нажмёт «Впустить по адресу» в своей комнате, а вы — «Повторить»."
                            : "Ваш внешний адрес узнать не удалось — подключиться так не получится. Проверьте интернет.",
                        "ДЛЯ ХОСТА"),
                    new StateOption("sliders", "Проброс порта",
                        $"Или другу нужно открыть на роутере UDP-порт своей комнаты (по умолчанию {AppSettings.DefaultAppPort}) и прислать адрес с этим портом.",
                        "ДЛЯ ХОСТА"),
                },
                MyAddress = myAddress,
                CopyMyAddressCommand = myAddress == null ? null : new RelayCommand(() =>
                {
                    if (AppServices.CopyToClipboard(myAddress)) shell.Toast("Ваш адрес скопирован — отправьте его другу");
                }),
                Details = details,
                DetailsOpen = true, // Для ошибок связи детали открыты сразу
                PrimaryLabel = "Повторить",
                PrimaryCommand = new RelayCommand(() => shell.Navigate(new ConnectingViewModel(shell, address))),
                SecondaryLabel = "Изменить адрес",
                SecondaryCommand = new RelayCommand(() => shell.StartJoin(address)),
            };
        }

        /// <summary>3g. Нет интернета или исходящий UDP заблокирован.</summary>
        public static StateViewModel NoInternet(ShellViewModel shell, string address, IReadOnlyList<string> details) => new()
        {
            Tone = StateTone.Error,
            Icon = "wifiOff",
            Title = "Нет доступа к интернету или UDP заблокирован",
            Description = "ConnectTogether не может связаться с интернетом. Чаще всего соединение блокирует брандмауэр " +
                          "или сеть на работе, в школе или общежитии.",
            Tips = Numbered(new[]
            {
                "Проверьте, открываются ли сайты в браузере.",
                "Разрешите ConnectTogether в брандмауэре Windows: «Безопасность Windows» → «Брандмауэр и защита сети» → " +
                "«Разрешить работу с приложением через брандмауэр».",
                "Если сеть общественная или рабочая, попробуйте другую — например, раздачу с телефона.",
            }),
            Details = details,
            PrimaryLabel = "Проверить снова",
            PrimaryCommand = new RelayCommand(() => shell.Navigate(new ConnectingViewModel(shell, address))),
            SecondaryLabel = "Открыть настройки брандмауэра",
            SecondaryCommand = new RelayCommand(AppLinks.OpenFirewallSettings),
        };

        /// <summary>3d. Сервер игры перестал отвечать при открытой комнате.</summary>
        public static StateViewModel ServerDown(ShellViewModel shell, HostRoomViewModel room, int silentSeconds)
        {
            var state = new StateViewModel
            {
                Tone = StateTone.Error,
                Icon = "server",
                Title = "Сервер игры не запущен",
                Description = $"Комната открыта, но сервер {room.Game.Name} перестал отвечать. Друзья не смогут зайти, пока он снова не заработает.",
                Tips = Numbered(room.Game.ServerStoppedTips),
                Details = new[] { $"Проверка: {room.Room.Server} — никто не слушает {silentSeconds} с" },
                PrimaryLabel = "Проверить снова",
                SecondaryLabel = "Закрыть комнату",
                SecondaryCommand = room.CloseCommand,
            };
            state.PrimaryCommand = new AsyncCommand(async () =>
            {
                state.PrimaryLabel = "Проверяем…";
                var minimum = Task.Delay(600);
                bool running = await room.RecheckServerAsync();
                await minimum;
                state.PrimaryLabel = "Проверить снова";
                if (running) shell.Navigate(room);
            });
            return state;
        }

        /// <summary>3e. Порт приложения занят другой программой.</summary>
        public static StateViewModel PortBusy(ShellViewModel shell, int port, string? owner) => new()
        {
            Tone = StateTone.Warning,
            Icon = "alert",
            Title = "Порт занят другой программой",
            Description = $"Порт {port}, через который работает ConnectTogether, уже использует другая программа — например, " +
                          "вторая копия ConnectTogether или другая программа для сетевой игры.",
            Tips = Numbered(new[]
            {
                "Закройте вторую копию ConnectTogether, если она открыта.",
                "Или выберите другой порт — свободный мы найдём сами.",
            }),
            Details = new[] { owner != null ? $"UDP 0.0.0.0:{port} занят процессом {owner}" : $"UDP 0.0.0.0:{port} занят другой программой" },
            PrimaryLabel = "Выбрать свободный порт",
            PrimaryCommand = new RelayCommand(() =>
            {
                int free = PortInspector.FindFreeUdpPort(port);
                shell.Services.Settings.Update(s => s.AppPort = free);
                Trace.WriteLine($"[State] App port changed {port} -> {free}");
                shell.Toast($"ConnectTogether теперь работает через порт {free}");
                shell.GoHome();
            }),
            SecondaryLabel = "Открыть настройки",
            SecondaryCommand = new RelayCommand(shell.ShowSettings),
        };
    }

    /// <summary>3f. Связь с хостом прервалась: обратный отсчёт и автоматическое переподключение.</summary>
    public sealed class ConnectionLostViewModel : StateViewModel, INavigationAware
    {
        private const int IntervalSeconds = 10;
        private const int MaxAttempts = 5;
        private readonly ShellViewModel _shell;
        private readonly PlayerRoomViewModel _room;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DateTime _lostAt = DateTime.Now;
        private int _seconds = IntervalSeconds;
        private int _attempt = 1;
        private bool _reconnecting;

        public ConnectionLostViewModel(ShellViewModel shell, PlayerRoomViewModel room)
        {
            _shell = shell;
            _room = room;
            Tone = StateTone.Warning;
            Icon = "wifiOff";
            Title = "Соединение потеряно";
            Description = "Связь с хостом прервалась. Переподключаемся автоматически — игру закрывать не нужно.";
            PrimaryLabel = "Переподключиться сейчас";
            PrimaryCommand = new AsyncCommand(ReconnectAsync, () => !_reconnecting);
            SecondaryLabel = "Выйти из комнаты";
            SecondaryCommand = room.LeaveCommand;
            _timer.Tick += async (_, _) => await TickAsync();
        }

        public override bool IsCountdown => true;

        public override int Seconds => _seconds;

        public override double Progress => _seconds / (double)IntervalSeconds;

        public override string CountdownTitle => _reconnecting ? "Переподключаемся…" : $"Переподключение через {_seconds} с";

        public override string AttemptText => $"Попытка {_attempt} из {MaxAttempts}";

        private void SetSeconds(int value)
        {
            if (_seconds == value) return;
            _seconds = value;
            OnPropertyChanged(nameof(Seconds));
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(CountdownTitle));
        }

        public override IReadOnlyList<string> Details => new[]
        {
            $"Последний ответ хоста: {(int)(DateTime.Now - _lostAt).TotalSeconds} с назад",
            $"Попытка {_attempt} из {MaxAttempts}, интервал {IntervalSeconds} с",
        };

        public void OnNavigatedTo() => _timer.Start();

        public void OnNavigatedFrom() => _timer.Stop();

        /// <summary>Для предпросмотра: значения из макета.</summary>
        public void ShowMockupState(int seconds, int attempt)
        {
            _attempt = attempt;
            SetSeconds(seconds);
            OnPropertyChanged(string.Empty);
        }

        private async Task TickAsync()
        {
            if (_reconnecting) return;
            OnPropertyChanged(nameof(Details));
            if (_seconds > 1)
            {
                SetSeconds(_seconds - 1);
                return;
            }
            await ReconnectAsync();
        }

        private async Task ReconnectAsync()
        {
            _reconnecting = true;
            OnPropertyChanged(nameof(CountdownTitle));
            bool ok;
            try
            {
                ok = await _room.Room.ReconnectAsync();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ConnectionLost] Reconnect attempt {_attempt} failed: {ex.Message}");
                ok = false;
            }
            _reconnecting = false;

            if (ok)
            {
                _shell.Navigate(_room);
                _shell.Toast("Соединение восстановлено");
                return;
            }
            if (_attempt >= MaxAttempts)
            {
                _shell.Toast("Не удалось переподключиться к хосту", ToastKind.Warning);
                await _room.LeaveAsync();
                return;
            }

            _attempt++;
            SetSeconds(IntervalSeconds);
            OnPropertyChanged(nameof(AttemptText));
            OnPropertyChanged(nameof(Details));
            OnPropertyChanged(nameof(CountdownTitle));
            RelayCommand.Refresh();
        }
    }
}
