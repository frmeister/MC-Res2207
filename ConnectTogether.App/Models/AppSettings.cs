// ConnectTogether.App/Models/AppSettings.cs

namespace ConnectTogether.App.Models
{
    public enum ThemeMode
    {
        Dark,
        Light,
        System,
    }

    /// <summary>Комната из списка «Недавние комнаты» на главном экране.</summary>
    public sealed class RecentRoom
    {
        public string IntegrationId { get; set; } = "";
        public bool IsHost { get; set; }

        /// <summary>Адрес комнаты IP:порт.</summary>
        public string Address { get; set; } = "";
        public string HostName { get; set; } = "";
        public int PlayerCount { get; set; }
        public DateTime Time { get; set; }
    }

    /// <summary>Настройки, которые сохраняются между запусками.</summary>
    public sealed class AppSettings
    {
        public const int DefaultAppPort = 47312;
        public const int MaxRecentRooms = 5;

        public string PlayerName { get; set; } = "";
        public ThemeMode Theme { get; set; } = ThemeMode.Dark;
        public int AppPort { get; set; } = DefaultAppPort;
        public string? LastIntegrationId { get; set; }
        public List<RecentRoom> RecentRooms { get; set; } = new();
    }
}
