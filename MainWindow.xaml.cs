using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Simple_PC_Manager
{
    public partial class MainWindow : Window
    {
        // Startup registry locations. The 32-bit Run key is read through the 64-bit view.
        private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Run32Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedBasePath =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        private const string DefaultHeroText = "Scan for junk files and review them before anything is deleted";

        private static readonly bool IsElevatedProcess = IsElevated();

        private static readonly HashSet<string> ProtectedNames = new HashSet<string>(
            new[]
            {
                // Windows core
                "system", "registry", "idle", "secure system",
                "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsm",
                "lsaiso", "svchost", "fontdrvhost", "dwm", "sihost",
                "ctfmon", "taskhostw", "shellexperiencehost",
                "startmenuexperiencehost", "searchhost", "runtimebroker",
                "sppsvc", "audiodg", "conhost", "wudfhost",
                "memory compression", "securekernel", "spoolsv",
                "explorer", "shelldllhost", "textinputhost", "dllhost",
                "searchindexer", "wmiprvse", "securityhealthservice", "securityhealthsystray",
                // Security software
                "msmpeng", "nissrv",
                // Databases / virtualization the user may depend on
                "sqlservr", "sqlwriter", "sqlbrowser", "vmmem", "vmmemwsl", "vmcompute"
            },
            StringComparer.OrdinalIgnoreCase);

        private enum SortMode { Memory, Name, Pid }

        private List<ProcessInfo> _all = new List<ProcessInfo>();
        private List<StartupItem> _startup = new List<StartupItem>();
        private readonly List<CleanCategory> _categories = JunkCleaner.CreateCategories();
        private DispatcherTimer? _timer;
        private CancellationTokenSource? _cts;
        private int _dashboardBusy;
        private int _procBusy;
        private bool _cleanerBusy;
        private bool _hideProtected;
        private SortMode _sort = SortMode.Memory;

        public MainWindow()
        {
            InitializeComponent();

            SampleCpu();   // prime the CPU counter so the first dashboard reading is a real delta

            ThemeToggle.IsChecked = ThemeManager.IsDark;
            var version = typeof(MainWindow).Assembly.GetName().Version;
            VersionText.Text = version != null ? "v" + version.ToString(3) : "v1.0";
            ElevateButton.Visibility = IsElevatedProcess ? Visibility.Collapsed : Visibility.Visible;

            long lifetime = AppSettings.GetTotalFreed();
            if (lifetime > 0)
                HeroStatus.Text = DefaultHeroText + "  -  " + JunkCleaner.FormatBytes(lifetime) + " freed so far";

            foreach (var c in _categories)
                c.PropertyChanged += Category_PropertyChanged;
            CleanList.ItemsSource = _categories;
            UpdateCleanerSummary();

            Loaded += async (_, _) =>
            {
                await RefreshProcessesAsync();
                await RefreshDashboardAsync();
                LoadDrives();
                LoadStartupItems();
                StartTimer();
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            UpdateTitleBarTheme(ThemeManager.IsDark);
        }

        private void Window_Closed(object? sender, EventArgs e)
        {
            _timer?.Stop();
            _timer = null;
            _cts?.Cancel();
        }

        private void UpdateTitleBarTheme(bool dark)
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int val = dark ? 1 : 0;
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 20H1+), 19 = older builds
                if (DwmSetWindowAttribute(hwnd, 20, ref val, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref val, sizeof(int));
            }
            catch { /* cosmetic only */ }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private static bool IsElevated()
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private void Elevate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;

                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
                Application.Current.Shutdown();
            }
            catch (Win32Exception)
            {
                // User cancelled the UAC prompt: stay in the current instance.
            }
        }

        private void StartTimer()
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _timer.Tick += async (_, _) =>
            {
                if (WindowState == WindowState.Minimized) return;
                if (DashboardPage.Visibility == Visibility.Visible)
                    await RefreshDashboardAsync();
            };
            _timer.Start();
        }

        // Pages: 0 Dashboard, 1 Cleaner, 2 Processes, 3 Storage, 4 Startup
        private async void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DashboardPage == null || StartupPage == null) return;

            int i = NavList.SelectedIndex;
            DashboardPage.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
            CleanerPage.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
            ProcessesPage.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
            StoragePage.Visibility = i == 3 ? Visibility.Visible : Visibility.Collapsed;
            StartupPage.Visibility = i == 4 ? Visibility.Visible : Visibility.Collapsed;

            if (i == 0) await RefreshDashboardAsync();
            if (i == 2) await RefreshProcessesAsync();
            if (i == 3) LoadDrives();
            if (i == 4) LoadStartupItems();
        }

        private void ThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            bool dark = ThemeToggle.IsChecked == true;
            ThemeManager.Apply(dark);
            UpdateTitleBarTheme(dark);
        }

        // ===== Keyboard shortcuts =====
        private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (e.Key == Key.F5)
            {
                e.Handled = true;
                await RefreshCurrentPageAsync();
            }
            else if (ctrl && e.Key >= Key.D1 && e.Key <= Key.D5)
            {
                e.Handled = true;
                NavList.SelectedIndex = e.Key - Key.D1;
            }
            else if (ctrl && e.Key == Key.F)
            {
                e.Handled = true;
                NavList.SelectedIndex = 2;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    FilterBox.Focus();
                    FilterBox.SelectAll();
                }));
            }
        }

        private async Task RefreshCurrentPageAsync()
        {
            switch (NavList.SelectedIndex)
            {
                case 0: await RefreshDashboardAsync(); break;
                case 2: await RefreshProcessesAsync(); break;
                case 3: LoadDrives(); break;
                case 4: LoadStartupItems(); break;
            }
        }

        // ===== Cleaner =====
        private async void ScanFromDashboard_Click(object sender, RoutedEventArgs e)
        {
            NavList.SelectedIndex = 1;
            await ScanAsync();
        }

        private async void Scan_Click(object sender, RoutedEventArgs e)
        {
            await ScanAsync();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var c in _categories) c.IsSelected = true;
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var c in _categories) c.IsSelected = false;
        }

        private void CancelCleaner_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            CleanerSummary.Text = "Cancelling...";
            CancelButton.IsEnabled = false;
        }

        private void Category_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_cleanerBusy) return;
            if (e.PropertyName == nameof(CleanCategory.IsSelected))
                UpdateCleanerSummary();
        }

        private void SetCleanerBusy(bool busy)
        {
            _cleanerBusy = busy;
            ScanButton.IsEnabled = !busy;
            SelectAllButton.IsEnabled = !busy;
            SelectNoneButton.IsEnabled = !busy;
            CancelButton.IsEnabled = true;
            CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            CleanProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy) CleanButton.IsEnabled = false;
        }

        private async Task ScanAsync()
        {
            if (_cleanerBusy) return;

            using var cts = new CancellationTokenSource();
            _cts = cts;
            SetCleanerBusy(true);
            CleanerSummary.Text = "Scanning...";

            foreach (var c in _categories)
                c.IsScanned = false;

            CleanProgress.Maximum = Math.Max(1, _categories.Count);
            CleanProgress.Value = 0;
            int done = 0;

            foreach (var c in _categories)
            {
                if (cts.IsCancellationRequested) break;

                CleanerSummary.Text = $"Scanning {c.Name}...";

                ScanResult r = default;
                try { r = await Task.Run(() => JunkCleaner.Measure(c, cts.Token)); }
                catch { /* leave at 0 */ }

                if (cts.IsCancellationRequested) break;   // partial counts are discarded

                c.Bytes = r.Bytes;
                c.FileCount = r.Files;
                c.IsScanned = true;
                CleanProgress.Value = ++done;
            }

            bool cancelled = cts.IsCancellationRequested;
            _cts = null;
            SetCleanerBusy(false);
            UpdateCleanerSummary();

            if (cancelled)
                CleanerSummary.Text = "Scan cancelled.  " + CleanerSummary.Text;
        }

        private void UpdateCleanerSummary()
        {
            bool scanned = _categories.Any(c => c.IsScanned);

            if (!scanned)
            {
                CleanerSummary.Text = "Press Scan to see what can be cleaned. Nothing is deleted until you confirm.";
                CleanButton.IsEnabled = false;
                return;
            }

            long found = _categories.Where(c => c.IsScanned).Sum(c => c.Bytes);
            long selected = _categories.Where(c => c.IsScanned && c.IsSelected).Sum(c => c.Bytes);

            CleanerSummary.Text =
                $"Found {JunkCleaner.FormatBytes(found)}  -  {JunkCleaner.FormatBytes(selected)} selected";
            CleanButton.IsEnabled = !_cleanerBusy && selected > 0;
        }

        private async void Clean_Click(object sender, RoutedEventArgs e)
        {
            if (_cleanerBusy) return;

            var chosen = _categories
                .Where(c => c.IsScanned && c.IsSelected && c.Bytes > 0)
                .ToList();
            if (chosen.Count == 0) return;

            long total = chosen.Sum(c => c.Bytes);
            string list = string.Join("\n", chosen.Select(c => $"  - {c.Name}  ({c.SizeText})"));
            string extra = chosen.Any(c => c.Kind == CleanKind.RecycleBin)
                ? "\n\nThe Recycle Bin will be emptied permanently."
                : "";

            var answer = MessageBox.Show(
                $"Delete about {JunkCleaner.FormatBytes(total)}?\n\n{list}{extra}\n\n" +
                "Files that are in use by other programs are skipped.",
                "Clean selected", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            using var cts = new CancellationTokenSource();
            _cts = cts;
            SetCleanerBusy(true);

            CleanProgress.Maximum = chosen.Count;
            CleanProgress.Value = 0;

            long freed = 0;
            int items = 0;
            int done = 0;
            string? error = null;

            try
            {
                foreach (var c in chosen)
                {
                    if (cts.IsCancellationRequested) break;

                    CleanerSummary.Text = $"Cleaning {c.Name}...";
                    var r = await Task.Run(() => JunkCleaner.Clean(c, cts.Token));
                    freed += r.Bytes;
                    items += r.Files;
                    CleanProgress.Value = ++done;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            bool cancelled = cts.IsCancellationRequested;
            _cts = null;
            SetCleanerBusy(false);

            long lifetime = AppSettings.AddFreed(freed);
            await ScanAsync();   // show what is left

            string freedText = $"{JunkCleaner.FormatBytes(freed)} ({items:N0} items)";
            string result = error != null
                ? "Clean stopped: " + error + $"  Freed {freedText} before that."
                : cancelled
                    ? $"Cancelled. Freed {freedText}."
                    : $"Freed {freedText}.";

            CleanerSummary.Text = result + "  " + CleanerSummary.Text;
            HeroStatus.Text = result + "  -  " + JunkCleaner.FormatBytes(lifetime) + " freed so far";
            await RefreshDashboardAsync();
        }

        // ===== Dashboard =====
        private sealed record DashboardSnapshot(
            double MemUsedGB, double MemTotalGB,
            double DiskUsedGB, double DiskTotalGB, double DiskFreeGB,
            int ProcessCount, double CpuPercent, TimeSpan Uptime);

        private static long _prevIdle, _prevKernel, _prevUser;
        private static double _lastCpu;

        /// <summary>System-wide CPU load since the previous call (0-100).</summary>
        private static double SampleCpu()
        {
            if (!GetSystemTimes(out long idle, out long kernel, out long user)) return _lastCpu;

            bool first = _prevKernel == 0 && _prevUser == 0;
            long dIdle = idle - _prevIdle;
            long dKernel = kernel - _prevKernel;
            long dUser = user - _prevUser;

            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;

            long busyPlusIdle = dKernel + dUser;   // kernel time already includes idle time
            if (first || busyPlusIdle <= 0) return _lastCpu;

            _lastCpu = Math.Clamp((busyPlusIdle - dIdle) * 100.0 / busyPlusIdle, 0, 100);
            return _lastCpu;
        }

        private static DashboardSnapshot CaptureDashboard()
        {
            double memUsed = 0, memTotal = 0;
            var mem = new MEMORYSTATUSEX();
            mem.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
            if (GlobalMemoryStatusEx(ref mem))
            {
                memTotal = mem.ullTotalPhys / 1024.0 / 1024 / 1024;
                memUsed = (mem.ullTotalPhys - mem.ullAvailPhys) / 1024.0 / 1024 / 1024;
            }

            double diskTotal = 0, diskFree = 0;
            try
            {
                var root = Path.GetPathRoot(Environment.SystemDirectory);
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady)
                    {
                        diskTotal = drive.TotalSize / 1024.0 / 1024 / 1024;
                        diskFree = drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;
                    }
                }
            }
            catch { }

            int count = 0;
            try
            {
                var procs = Process.GetProcesses();
                count = procs.Length;
                foreach (var p in procs) p.Dispose();
            }
            catch { }

            return new DashboardSnapshot(
                memUsed, memTotal, diskTotal - diskFree, diskTotal, diskFree, count,
                SampleCpu(), TimeSpan.FromMilliseconds(Environment.TickCount64));
        }

        private static string FormatUptime(TimeSpan t)
        {
            if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
            if (t.TotalHours >= 1) return $"{t.Hours}h {t.Minutes}m";
            return $"{t.Minutes}m";
        }

        /// <summary>Turns a bar red when it is nearly full; follows theme changes.</summary>
        private static void SetBarLevel(ProgressBar bar, double pct)
        {
            bar.SetResourceReference(ProgressBar.ForegroundProperty, pct >= 90 ? "DangerBrush" : "AccentBrush");
        }

        private async Task RefreshDashboardAsync()
        {
            // Skip if the previous refresh is still running
            if (Interlocked.Exchange(ref _dashboardBusy, 1) == 1) return;

            try
            {
                var s = await Task.Run(CaptureDashboard);

                CpuUsageText.Text = $"{s.CpuPercent:F0}%";
                CpuBar.Value = Math.Clamp(s.CpuPercent, 0, 100);
                SetBarLevel(CpuBar, s.CpuPercent);
                CpuDetailText.Text = $"{Environment.ProcessorCount} logical cores - up {FormatUptime(s.Uptime)}";

                if (s.MemTotalGB > 0)
                {
                    double pct = s.MemUsedGB / s.MemTotalGB * 100;
                    MemUsageText.Text = $"{s.MemUsedGB:F1} / {s.MemTotalGB:F0} GB";
                    MemDetailText.Text = $"{pct:F0}% in use - {s.MemTotalGB - s.MemUsedGB:F1} GB free";
                    MemBar.Value = Math.Clamp(pct, 0, 100);
                    SetBarLevel(MemBar, pct);
                }

                if (s.DiskTotalGB > 0)
                {
                    double pct = s.DiskUsedGB / s.DiskTotalGB * 100;
                    DiskUsageText.Text = $"{s.DiskUsedGB:F0} / {s.DiskTotalGB:F0} GB";
                    DiskDetailText.Text = $"{pct:F0}% used - {s.DiskFreeGB:F0} GB free";
                    DiskBar.Value = Math.Clamp(pct, 0, 100);
                    SetBarLevel(DiskBar, pct);
                }

                ProcCountText.Text = s.ProcessCount.ToString();
                ProcDetailText.Text = $"Running on {Environment.MachineName}";
            }
            finally
            {
                Volatile.Write(ref _dashboardBusy, 0);
            }
        }

        // ===== Processes =====
        private static List<ProcessInfo> CaptureProcesses()
        {
            var result = new List<ProcessInfo>();
            int ownPid = Environment.ProcessId;

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string name = p.ProcessName ?? "";
                    double mb = 0;
                    try { mb = Math.Round(p.WorkingSet64 / 1024.0 / 1024.0, 1); }
                    catch { /* access denied */ }

                    result.Add(new ProcessInfo
                    {
                        ProcessName = name,
                        Initial = name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?",
                        Id = p.Id,
                        WorkingSetMB = mb,
                        IsProtected = p.Id == ownPid || ProtectedNames.Contains(name)
                    });
                }
                catch { /* process exited while we were reading it */ }
                finally { p.Dispose(); }
            }

            return result;
        }

        private async Task RefreshProcessesAsync()
        {
            if (Interlocked.Exchange(ref _procBusy, 1) == 1) return;

            try
            {
                _all = await Task.Run(CaptureProcesses);
                ApplyFilter();

                string role = IsElevatedProcess ? "Administrator" : "Standard user";
                StatusText.Text =
                    $"{role}  -  {_all.Count} processes  -  {Environment.MachineName}  -  " +
                    $"{Environment.ProcessorCount} cores  -  {RuntimeInformation.OSDescription}";
            }
            finally
            {
                Volatile.Write(ref _procBusy, 0);
            }
        }

        private void ApplyFilter()
        {
            if (FilterBox == null || ProcessList == null || ProcSubtitle == null) return;

            IEnumerable<ProcessInfo> q = _all;

            var text = FilterBox.Text.Trim();
            if (text.Length > 0)
            {
                q = q.Where(p =>
                    p.ProcessName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    p.Id.ToString() == text);
            }

            if (_hideProtected)
                q = q.Where(p => !p.IsProtected);

            q = _sort switch
            {
                SortMode.Name => q.OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                                  .ThenByDescending(p => p.WorkingSetMB),
                SortMode.Pid => q.OrderBy(p => p.Id),
                _ => q.OrderByDescending(p => p.WorkingSetMB)
                      .ThenBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            };

            var list = q.ToList();
            ProcessList.ItemsSource = list;

            double totalMb = list.Sum(p => p.WorkingSetMB);
            string order = _sort switch
            {
                SortMode.Name => "by name",
                SortMode.Pid => "by PID",
                _ => "highest memory first"
            };
            string mem = totalMb >= 1024 ? $"{totalMb / 1024:F1} GB" : $"{totalMb:F0} MB";
            ProcSubtitle.Text = $"{list.Count} shown, {mem} in use - sorted {order}";
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void SortButton_Click(object sender, RoutedEventArgs e)
        {
            _sort = _sort switch
            {
                SortMode.Memory => SortMode.Name,
                SortMode.Name => SortMode.Pid,
                _ => SortMode.Memory
            };

            SortButton.Content = _sort switch
            {
                SortMode.Name => "Sort: Name",
                SortMode.Pid => "Sort: PID",
                _ => "Sort: Memory"
            };

            ApplyFilter();
        }

        private void HideProtectedToggle_Click(object sender, RoutedEventArgs e)
        {
            _hideProtected = HideProtectedToggle.IsChecked == true;
            ApplyFilter();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshProcessesAsync();
            LoadDrives();
            await RefreshDashboardAsync();
        }

        private async void EndProcessRow_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ProcessInfo item) return;

            if (item.IsProtected)
            {
                MessageBox.Show(
                    $"{item.ProcessName} is a protected process and won't be ended.\n\n" +
                    "Ending it can crash Windows or this app immediately.",
                    "Blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var answer = MessageBox.Show(
                $"End \"{item.ProcessName}\" (PID {item.Id})?\n\nUnsaved data in it will be lost.",
                "End process", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            bool ok = await Task.Run(() => ForceKill(item.Id));
            if (!ok)
            {
                MessageBox.Show(
                    $"Could not end {item.ProcessName}." +
                    (IsElevatedProcess ? "" : "\n\nTry \"Restart as administrator\" at the bottom of the window."),
                    "Simple PC Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            await RefreshProcessesAsync();
        }

        private static bool ForceKill(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill();
                p.WaitForExit(2000);
                return true;
            }
            catch (ArgumentException)
            {
                return true; // already gone
            }
            catch (InvalidOperationException)
            {
                return true; // exited while we were killing it
            }
            catch { /* fall through to taskkill */ }

            try
            {
                var psi = new ProcessStartInfo("taskkill", $"/F /T /PID {pid}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var tk = Process.Start(psi);
                if (tk == null) return false;
                if (!tk.WaitForExit(4000)) return false;
                return tk.ExitCode == 0;
            }
            catch { return false; }
        }

        // ===== Quick actions =====
        private void LaunchShell(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not open {target}: {ex.Message}";
            }
        }

        private void OpenTaskManager_Click(object sender, RoutedEventArgs e) => LaunchShell("taskmgr.exe");

        private void OpenSettings_Click(object sender, RoutedEventArgs e) => LaunchShell("ms-settings:");

        private void OpenApps_Click(object sender, RoutedEventArgs e) => LaunchShell("ms-settings:appsfeatures");

        private void OpenDiskCleanup_Click(object sender, RoutedEventArgs e) => LaunchShell("cleanmgr.exe");

        private void OpenDrive_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DriveItem d)
                LaunchShell(d.RootPath);
        }

        private void ExportReport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Export system report",
                FileName = $"PC-report-{DateTime.Now:yyyyMMdd-HHmm}.txt",
                Filter = "Text file (*.txt)|*.txt",
                DefaultExt = ".txt",
                AddExtension = true
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                File.WriteAllText(dlg.FileName, BuildReport(), new UTF8Encoding(false));
                StatusText.Text = "Report saved: " + dlg.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save the report:\n" + ex.Message,
                    "Simple PC Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string BuildReport()
        {
            var s = CaptureDashboard();
            var sb = new StringBuilder();

            sb.AppendLine("Simple PC Manager - system report");
            sb.AppendLine($"Created: {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine(new string('=', 48));
            sb.AppendLine();
            sb.AppendLine("SYSTEM");
            sb.AppendLine($"  Machine:     {Environment.MachineName}");
            sb.AppendLine($"  OS:          {RuntimeInformation.OSDescription}");
            sb.AppendLine($"  Runtime:     {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"  Cores:       {Environment.ProcessorCount} logical");
            sb.AppendLine($"  Uptime:      {FormatUptime(s.Uptime)}");
            sb.AppendLine($"  Privileges:  {(IsElevatedProcess ? "Administrator" : "Standard user")}");
            sb.AppendLine($"  CPU load:    {s.CpuPercent:F0}%");
            sb.AppendLine($"  Memory:      {s.MemUsedGB:F1} / {s.MemTotalGB:F1} GB in use");
            sb.AppendLine($"  Processes:   {s.ProcessCount}");
            sb.AppendLine();

            sb.AppendLine("DRIVES");
            var drives = ReadDrives();
            if (drives.Count == 0) sb.AppendLine("  (none found)");
            foreach (var d in drives)
                sb.AppendLine($"  {d.Name,-4} {d.Label,-20} {d.FreeText}, {d.TotalText} ({d.UsedPercent:F0}% used)");
            sb.AppendLine();

            sb.AppendLine("TOP 15 PROCESSES BY MEMORY");
            foreach (var p in _all.OrderByDescending(x => x.WorkingSetMB).Take(15))
                sb.AppendLine($"  {p.WorkingSetMB,9:N1} MB  {p.ProcessName} (PID {p.Id})");
            sb.AppendLine();

            sb.AppendLine("STARTUP ITEMS");
            if (_startup.Count == 0) sb.AppendLine("  (none found)");
            foreach (var st in _startup)
                sb.AppendLine($"  [{(st.IsEnabled ? "on " : "off")}] {st.Name}  <{st.Source}>  {st.Command}");

            return sb.ToString();
        }

        // ===== Storage =====
        private static List<DriveItem> ReadDrives()
        {
            var list = new List<DriveItem>();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        // Check the type first: IsReady can be slow on unavailable network drives.
                        if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
                        if (!d.IsReady) continue;

                        double totalGB = d.TotalSize / 1024.0 / 1024 / 1024;
                        double freeGB = d.AvailableFreeSpace / 1024.0 / 1024 / 1024;
                        double usedGB = totalGB - freeGB;
                        double pct = totalGB > 0 ? usedGB / totalGB * 100 : 0;
                        pct = Math.Clamp(pct, 0, 100);

                        list.Add(new DriveItem
                        {
                            Name = d.Name.TrimEnd('\\'),
                            RootPath = d.Name,
                            Label = string.IsNullOrEmpty(d.VolumeLabel)
                                ? (d.DriveType == DriveType.Removable ? "Removable Disk" : "Local Disk")
                                : d.VolumeLabel,
                            FreeText = $"{freeGB:F0} GB free",
                            TotalText = $"of {totalGB:F0} GB",
                            UsedPercent = pct,
                            IsLow = pct >= 90
                        });
                    }
                    catch { /* drive vanished mid-read */ }
                }
            }
            catch { }

            return list;
        }

        private void LoadDrives()
        {
            DriveList.ItemsSource = ReadDrives();
        }

        // ===== Startup =====
        private void RefreshStartup_Click(object sender, RoutedEventArgs e)
        {
            LoadStartupItems();
        }

        private void LoadStartupItems()
        {
            var list = new List<StartupItem>();

            var sources = new[]
            {
                (Hive: RegistryHive.CurrentUser,  Path: RunPath,   Approved: "Run",   Label: "HKCU"),
                (Hive: RegistryHive.LocalMachine, Path: RunPath,   Approved: "Run",   Label: "HKLM"),
                (Hive: RegistryHive.LocalMachine, Path: Run32Path, Approved: "Run32", Label: "HKLM (32-bit)")
            };

            foreach (var s in sources)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(s.Hive, RegistryView.Registry64);
                    using var key = baseKey.OpenSubKey(s.Path);
                    if (key == null) continue;

                    foreach (var name in key.GetValueNames())
                    {
                        list.Add(new StartupItem
                        {
                            Name = name,
                            ValueName = name,
                            Command = key.GetValue(name)?.ToString() ?? "",
                            Source = s.Label,
                            ApprovedKey = s.Approved,
                            Hive = s.Hive,
                            IsEnabled = ReadApproved(s.Hive, s.Approved, name)
                        });
                    }
                }
                catch { /* key unreadable: skip this source */ }
            }

            // Shortcuts in the current user's Startup folder (Windows tracks these under "StartupFolder").
            try
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        try
                        {
                            var attrs = File.GetAttributes(file);
                            if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;   // desktop.ini

                            string fileName = Path.GetFileName(file);
                            list.Add(new StartupItem
                            {
                                Name = Path.GetFileNameWithoutExtension(fileName),
                                ValueName = fileName,
                                Command = file,
                                Source = "Startup folder",
                                ApprovedKey = "StartupFolder",
                                Hive = RegistryHive.CurrentUser,
                                IsEnabled = ReadApproved(RegistryHive.CurrentUser, "StartupFolder", fileName)
                            });
                        }
                        catch { /* skip unreadable entry */ }
                    }
                }
            }
            catch { /* folder unreadable: skip this source */ }

            _startup = list
                .OrderBy(x => x.Source, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            StartupList.ItemsSource = _startup;
            UpdateStartupSummary();
        }

        private void UpdateStartupSummary()
        {
            int on = _startup.Count(x => x.IsEnabled);
            int off = _startup.Count - on;
            StartupSummary.Text = $"{_startup.Count} items  -  {on} enabled  -  {off} disabled";
        }

        /// <summary>
        /// Reads Windows' own enabled/disabled state (the same one Task Manager uses).
        /// The per-user value wins; otherwise the machine-wide value; otherwise enabled.
        /// First byte: even (02/06) = enabled, odd (03/07) = disabled.
        /// </summary>
        private static bool ReadApproved(RegistryHive itemHive, string approvedKey, string name)
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                if (hive == RegistryHive.LocalMachine && itemHive != RegistryHive.LocalMachine) continue;

                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                    using var key = baseKey.OpenSubKey(ApprovedBasePath + approvedKey);
                    if (key?.GetValue(name) is byte[] data && data.Length > 0)
                        return (data[0] & 1) == 0;
                }
                catch { }
            }

            return true;
        }

        private static void WriteApproved(string approvedKey, string name, bool enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(ApprovedBasePath + approvedKey, true);
            var data = new byte[12];
            if (enabled)
            {
                data[0] = 0x02;
            }
            else
            {
                data[0] = 0x03;
                BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            }
            key.SetValue(name, data, RegistryValueKind.Binary);
        }

        private void StartupToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton tb || tb.DataContext is not StartupItem item) return;

            bool target = tb.IsChecked == true;
            try
            {
                WriteApproved(item.ApprovedKey, item.ValueName, target);
                item.IsEnabled = target;
                UpdateStartupSummary();
            }
            catch (Exception ex)
            {
                tb.IsChecked = !target;
                MessageBox.Show("Could not change startup state:\n" + ex.Message,
                    "Simple PC Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ===== Win32 =====
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        // FILETIME values are 64-bit tick counts, so plain longs marshal correctly.
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);
    }

    public class ProcessInfo
    {
        public string ProcessName { get; set; } = "";
        public string Initial { get; set; } = "?";
        public int Id { get; set; }
        public double WorkingSetMB { get; set; }
        public bool IsProtected { get; set; }
    }

    public class DriveItem
    {
        public string Name { get; set; } = "";
        public string RootPath { get; set; } = "";
        public string Label { get; set; } = "";
        public string FreeText { get; set; } = "";
        public string TotalText { get; set; } = "";
        public double UsedPercent { get; set; }
        public bool IsLow { get; set; }
    }

    public class StartupItem : INotifyPropertyChanged
    {
        private bool _enabled;

        /// <summary>Text shown in the list.</summary>
        public string Name { get; set; } = "";

        /// <summary>The registry value / file name Windows tracks this item under.</summary>
        public string ValueName { get; set; } = "";

        public string Command { get; set; } = "";
        public string Source { get; set; } = "";
        public string ApprovedKey { get; set; } = "Run";
        public RegistryHive Hive { get; set; }

        public bool IsEnabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
