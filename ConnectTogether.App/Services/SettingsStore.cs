// ConnectTogether.App/Services/SettingsStore.cs

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConnectTogether.App.Models;

namespace ConnectTogether.App.Services
{
    /// <summary>
    /// Хранит настройки в %APPDATA%\ConnectTogether\settings.json.
    /// Повреждённый или отсутствующий файл не мешает запуску — берутся значения по умолчанию.
    /// </summary>
    public sealed class SettingsStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly string _path;

        public SettingsStore(string? path = null)
        {
            _path = path ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConnectTogether", "settings.json");
            Current = Load();
        }

        public AppSettings Current { get; }

        public event EventHandler? Changed;

        /// <summary>Меняет настройки и сразу сохраняет их.</summary>
        public void Update(Action<AppSettings> change)
        {
            change(Current);
            Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Добавляет комнату в начало списка недавних; та же комната (по адресу и роли) не дублируется.</summary>
        public void AddRecentRoom(RecentRoom room) => Update(s =>
        {
            s.RecentRooms.RemoveAll(r => r.Address == room.Address && r.IsHost == room.IsHost);
            s.RecentRooms.Insert(0, room);
            if (s.RecentRooms.Count > AppSettings.MaxRecentRooms)
                s.RecentRooms.RemoveRange(AppSettings.MaxRecentRooms, s.RecentRooms.Count - AppSettings.MaxRecentRooms);
        });

        private AppSettings Load()
        {
            try
            {
                if (File.Exists(_path))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"[SettingsStore] Failed to load {_path}: {ex.Message}");
            }
            return new AppSettings();
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"[SettingsStore] Failed to save {_path}: {ex.Message}");
            }
        }
    }
}
