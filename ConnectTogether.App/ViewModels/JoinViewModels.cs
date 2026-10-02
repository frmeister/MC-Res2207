// ConnectTogether.App/ViewModels/JoinViewModels.cs

using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using ConnectTogether.App.Models;
using ConnectTogether.App.Mvvm;
using ConnectTogether.App.Services;

namespace ConnectTogether.App.ViewModels
{
    /// <summary>Присоединение по адресу хоста (1g).</summary>
    public sealed class JoinViewModel : ObservableObject
    {
        private readonly ShellViewModel _shell;
        private string _address = "";
        private string? _error;

        /// <param name="error">Почему вернулись на этот экран: комната заполнена, другая версия.</param>
        public JoinViewModel(ShellViewModel shell, string? address, string? error = null)
        {
            _shell = shell;
            _address = address ?? "";
            _error = error;
            ConnectCommand = new RelayCommand(Connect);
            BackCommand = new RelayCommand(shell.GoHome);
        }

        /// <summary>Адрес как ввёл пользователь; вставленное сообщение от друга экран заменяет адресом из него.</summary>
        public string Address
        {
            get => _address;
            set
            {
                if (Set(ref _address, value)) Error = null;
            }
        }

        public string? Error
        {
            get => _error;
            private set
            {
                if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError));
            }
        }

        public bool HasError => Error != null;

        public ICommand ConnectCommand { get; }
        public ICommand BackCommand { get; }

        private void Connect()
        {
            if (!RoomAddress.TryParse(Address, out var host, out var port, out var error))
            {
                Error = error;
                return;
            }
            _shell.Navigate(new ConnectingViewModel(_shell, RoomAddress.Format(host, port)));
        }
    }

    public enum StepState
    {
        Pending,
        Active,
        Done,
    }

    public sealed class ConnectStep : ObservableObject
    {
        private StepState _state;
        private string? _note;

        public ConnectStep(string title, bool isLast = false)
        {
            Title = title;
            IsLast = isLast;
        }

        public string Title { get; }
        public bool IsLast { get; }

        public StepState State
        {
            get => _state;
            set => Set(ref _state, value);
        }

        public string? Note
        {
            get => _note;
            set => Set(ref _note, value);
        }
    }

    /// <summary>Подключение к комнате (1h): три этапа, прогресс и подсказка, что всё идёт нормально.</summary>
    public sealed class ConnectingViewModel : ObservableObject, INavigationAware
    {
        private const int UsualSeconds = 30;
        private readonly ShellViewModel _shell;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private DateTime _stageStart = DateTime.Now;
        private CancellationTokenSource? _cts;
        private const int ShowMyAddressAfterSeconds = 8;
        private readonly DispatcherTimer _copiedTimer = new() { Interval = TimeSpan.FromSeconds(1.8) };
        private RoomInfo? _room;
        private string? _myAddress;
        private bool _isCopied;
        private bool _isPreview;

        public ConnectingViewModel(ShellViewModel shell, string address)
        {
            _shell = shell;
            Address = address;
            Steps = new[]
            {
                new ConnectStep("Ищем ваш адрес в интернете"),
                new ConnectStep("Связываемся с хостом"),
                new ConnectStep("Готово", isLast: true),
            };
            _timer.Tick += (_, _) => Tick();
            _copiedTimer.Tick += (_, _) =>
            {
                _copiedTimer.Stop();
                IsCopied = false;
            };
            CancelCommand = new RelayCommand(Cancel);
            CopyMyAddressCommand = new RelayCommand(CopyMyAddress);
        }

        public string Address { get; }

        public IReadOnlyList<ConnectStep> Steps { get; }

        /// <summary>«Rust · » перед моноширинным адресом, когда хост ответил; до этого — «Адрес ».</summary>
        public string SubtitlePrefix => _room != null ? $"{_room.Game.Name} · " : "Адрес ";

        public string HostHint => _room != null
            ? $"Хост тоже должен быть в комнате: если {_room.HostName} закроет ConnectTogether, подключиться не получится."
            : "Хост тоже должен быть в комнате: если закрыть ConnectTogether у хоста, подключиться не получится.";

        /// <summary>Доля полосы прогресса этапа «Связываемся с хостом».</summary>
        public double Progress => Math.Min(0.95, (DateTime.Now - _stageStart).TotalSeconds / UsualSeconds);

        public string ElapsedText
        {
            get
            {
                int s = (int)(DateTime.Now - _stageStart).TotalSeconds;
                string passed = RussianText.Plural(s, $"Прошла {s} секунда", $"Прошли {s} секунды", $"Прошло {s} секунд");
                return s <= UsualSeconds ? $"{passed} · обычно занимает до {UsualSeconds}" : $"{passed} · ждём хоста до 2 минут";
            }
        }

        /// <summary>Свой внешний адрес: если сеть хоста не пропускает входящие, хост впускает игрока по нему.</summary>
        public string? MyAddress => _myAddress;

        /// <summary>Хост долго молчит — предлагаем отправить ему свой адрес.</summary>
        public bool ShowMyAddress => _myAddress != null && Steps[1].State == StepState.Active &&
                                     (DateTime.Now - _stageStart).TotalSeconds >= ShowMyAddressAfterSeconds;

        public bool IsCopied
        {
            get => _isCopied;
            private set
            {
                if (Set(ref _isCopied, value)) OnPropertyChanged(nameof(CopyLabel));
            }
        }

        public string CopyLabel => IsCopied ? "Скопировано" : "Скопировать";

        public ICommand CancelCommand { get; }
        public ICommand CopyMyAddressCommand { get; }

        public void OnNavigatedTo()
        {
            if (!_isPreview) _ = RunAsync();
        }

        public void OnNavigatedFrom()
        {
            _timer.Stop();
            _cts?.Cancel();
        }

        private async Task RunAsync()
        {
            _cts = new CancellationTokenSource();
            var started = Stopwatch.StartNew();
            Steps[0].State = StepState.Active;
            _timer.Start();

            var progress = new Progress<JoinProgress>(p =>
            {
                if (p.MyAddress != null && _myAddress == null)
                {
                    _myAddress = p.MyAddress;
                    OnPropertyChanged(nameof(MyAddress));
                }
                if (p.Room != null && _room == null)
                {
                    _room = p.Room;
                    OnPropertyChanged(nameof(SubtitlePrefix));
                    OnPropertyChanged(nameof(HostHint));
                }
                if (p.Stage >= JoinStage.ContactingHost && Steps[0].State != StepState.Done)
                {
                    int s = Math.Max(1, (int)Math.Round(started.Elapsed.TotalSeconds));
                    Steps[0].State = StepState.Done;
                    Steps[0].Note = $"Готово за {s} {RussianText.Plural(s, "секунду", "секунды", "секунд")}";
                    Steps[1].State = StepState.Active;
                    _stageStart = DateTime.Now;
                    Tick();
                }
                if (p.Stage == JoinStage.Done)
                {
                    Steps[1].State = StepState.Done;
                    Steps[1].Note = null;
                    Steps[2].State = StepState.Done;
                }
            });

            try
            {
                var room = await _shell.Services.Rooms.JoinRoomAsync(Address, _shell.Services.PlayerName, progress, _cts.Token);
                _shell.Services.Settings.AddRecentRoom(new RecentRoom
                {
                    IntegrationId = room.Game.Id, IsHost = false, Address = room.Address, HostName = room.HostName, Time = DateTime.Now,
                });
                _shell.EnterRoom(new PlayerRoomViewModel(_shell, room));
            }
            catch (OperationCanceledException)
            {
                // Отменил пользователь — экран уже сменился
            }
            catch (RoomJoinException ex)
            {
                Trace.WriteLine($"[Connecting] Join {Address} failed: {ex.Failure}");
                switch (ex.Failure)
                {
                    case JoinFailure.RoomFull:
                        _shell.StartJoin(Address, "Комната заполнена: в ней уже максимум игроков.");
                        break;
                    case JoinFailure.IncompatibleVersion:
                        _shell.StartJoin(Address, "У хоста другая версия ConnectTogether — установите одинаковые версии.");
                        break;
                    case JoinFailure.NoInternet:
                        _shell.Navigate(StateViewModel.NoInternet(_shell, Address, ex.Details));
                        break;
                    default:
                        _shell.Navigate(StateViewModel.NoAnswer(_shell, Address, ex.Details, ex.MyAddress ?? _myAddress));
                        break;
                }
            }
            finally
            {
                _timer.Stop();
            }
        }

        private void Tick()
        {
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(ElapsedText));
            OnPropertyChanged(nameof(ShowMyAddress));
        }

        private void CopyMyAddress()
        {
            if (_myAddress == null || !AppServices.CopyToClipboard(_myAddress)) return;
            IsCopied = true;
            _copiedTimer.Stop();
            _copiedTimer.Start();
        }

        private void Cancel()
        {
            _cts?.Cancel();
            _shell.StartJoin(Address);
        }

        /// <summary>Для предпросмотра: этап 2 из 3, как в макете; подключение не запускается.</summary>
        public void ShowMockupState(RoomInfo? room, TimeSpan elapsed, string? myAddress = null)
        {
            _isPreview = true;
            _room = room;
            _myAddress = myAddress;
            Steps[0].State = StepState.Done;
            Steps[0].Note = "Готово за 2 секунды";
            Steps[1].State = StepState.Active;
            _stageStart = DateTime.Now - elapsed;
            OnPropertyChanged(string.Empty);
        }
    }
}
