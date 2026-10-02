// ConnectTogether.App/Services/ThemeService.cs

using System.Windows;
using ConnectTogether.App.Models;
using Microsoft.Win32;

namespace ConnectTogether.App.Services
{
    /// <summary>
    /// Переключает тему: первый словарь ресурсов приложения — токены Dark.xaml или Light.xaml.
    /// Режим «Как в Windows» читает настройку «Режим приложений» и следит за её изменением.
    /// </summary>
    public sealed class ThemeService
    {
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private ThemeMode _mode = ThemeMode.Dark;

        public ThemeService()
        {
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (_mode == ThemeMode.System && e.Category == UserPreferenceCategory.General)
                    Application.Current?.Dispatcher.BeginInvoke(() => Apply(ThemeMode.System));
            };
        }

        public bool IsLight { get; private set; }

        public void Apply(ThemeMode mode)
        {
            _mode = mode;
            IsLight = mode == ThemeMode.Light || (mode == ThemeMode.System && SystemUsesLightTheme());

            var dictionaries = Application.Current.Resources.MergedDictionaries;
            var tokens = new ResourceDictionary
            {
                Source = new Uri(IsLight ? "pack://application:,,,/Themes/Light.xaml" : "pack://application:,,,/Themes/Dark.xaml"),
            };
            if (dictionaries.Count > 0) dictionaries[0] = tokens;
            else dictionaries.Add(tokens);
        }

        private static bool SystemUsesLightTheme()
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
    }
}
