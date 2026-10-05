using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Simple_PC_Manager
{
    public partial class App : Application
    {
        private bool _showingError;

        internal static string LogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimplePCManager", "error.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) => LogError(args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                LogError(args.Exception);
                args.SetObserved();
            };

            // Apply the saved (or Windows) theme BEFORE the main window is created.
            ThemeManager.Initialize();

            base.OnStartup(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogError(e.Exception);
            e.Handled = true;

            if (_showingError) return;   // avoid a message-box storm if the error repeats
            _showingError = true;
            try
            {
                MessageBox.Show(
                    "Something went wrong, but the app is still running.\n\n" +
                    e.Exception.Message + "\n\nDetails were saved to:\n" + LogPath,
                    "Simple PC Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _showingError = false; }
        }

        private static void LogError(Exception? ex)
        {
            if (ex == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"[{DateTime.Now:s}] {ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { /* logging must never throw */ }
        }
    }
}
