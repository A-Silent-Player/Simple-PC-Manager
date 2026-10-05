using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Simple_PC_Manager
{
    public static class ThemeManager
    {
        private const string SettingsKeyPath = @"Software\SimplePCManager";
        private const string DarkModeValue = "DarkMode";
        private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        public static bool IsDark { get; private set; }

        /// <summary>Uses the saved choice, otherwise follows the Windows app theme.</summary>
        public static void Initialize()
        {
            ApplyCore(LoadSavedPreference() ?? SystemUsesDarkMode(), save: false);
        }

        /// <summary>Applies a theme chosen by the user and remembers it.</summary>
        public static void Apply(bool dark) => ApplyCore(dark, save: true);

        private static void ApplyCore(bool dark, bool save)
        {
            IsDark = dark;
            var r = Application.Current.Resources;

            Set(r, "BgBrush", dark ? "#1A1A1A" : "#F3F3F3");
            Set(r, "SidebarBrush", dark ? "#1F1F1F" : "#FBFBFB");
            Set(r, "CardBrush", dark ? "#262626" : "#FFFFFF");
            Set(r, "StrokeBrush", dark ? "#333333" : "#E5E5E5");
            Set(r, "StrokeStrongBrush", dark ? "#4A4A4A" : "#CCCCCC");
            Set(r, "StrokeSoftBrush", dark ? "#2A2A2A" : "#EFEFEF");
            Set(r, "TextBrush", dark ? "#F0F0F0" : "#1A1A1A");
            Set(r, "MutedBrush", dark ? "#A0A0A0" : "#616161");
            Set(r, "AccentBrush", dark ? "#4CA0E0" : "#0067C0");
            Set(r, "AccentHoverBrush", dark ? "#63B0E8" : "#1975C5");
            Set(r, "AccentSoftBrush", dark ? "#1E3A5F" : "#E5F1FB");
            Set(r, "AccentTextBrush", dark ? "#FFFFFF" : "#0067C0");
            Set(r, "NavHoverBrush", dark ? "#2A2A2A" : "#F0F0F0");
            Set(r, "NavSelectedBrush", dark ? "#1E3A5F" : "#E5F1FB");
            Set(r, "TrackBrush", dark ? "#333333" : "#EAEAEA");

            if (save) SavePreference(dark);
        }

        private static void Set(ResourceDictionary r, string key, string hex)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);

            if (r[key] is SolidColorBrush existing && !existing.IsFrozen)
                existing.Color = color;
            else
                r[key] = new SolidColorBrush(color);
        }

        private static bool SystemUsesDarkMode()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
                return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        private static bool? LoadSavedPreference()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
                return key?.GetValue(DarkModeValue) is int v ? v != 0 : null;
            }
            catch { return null; }
        }

        private static void SavePreference(bool dark)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath);
                key.SetValue(DarkModeValue, dark ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { /* preference just won't persist */ }
        }
    }
}