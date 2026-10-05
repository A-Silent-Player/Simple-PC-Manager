using Microsoft.Win32;

namespace Simple_PC_Manager
{
    /// <summary>Small persistent counters kept next to the theme preference in HKCU.</summary>
    public static class AppSettings
    {
        private const string KeyPath = @"Software\SimplePCManager";
        private const string TotalFreedValue = "TotalFreedBytes";

        public static long GetTotalFreed()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                return key?.GetValue(TotalFreedValue) is long v && v > 0 ? v : 0;
            }
            catch { return 0; }
        }

        public static long AddFreed(long bytes)
        {
            long total = GetTotalFreed();
            if (bytes <= 0) return total;

            total += bytes;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
                key.SetValue(TotalFreedValue, total, RegistryValueKind.QWord);
            }
            catch { /* counter just won't persist */ }

            return total;
        }
    }
}
