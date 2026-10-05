using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Simple_PC_Manager
{
    public enum CleanKind { Folders, RecycleBin }

    /// <summary>Bytes and number of files/items found or removed.</summary>
    public readonly record struct ScanResult(long Bytes, int Files);

    /// <summary>One row on the Cleaner page.</summary>
    public sealed class CleanCategory : INotifyPropertyChanged
    {
        private bool _selected;
        private long _bytes;
        private int _files;
        private bool _scanned;

        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public CleanKind Kind { get; init; } = CleanKind.Folders;
        public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();

        /// <summary>Only files and folders older than this are touched (zero = everything).</summary>
        public TimeSpan MinAge { get; init; } = TimeSpan.Zero;

        public bool IsSelected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                Raise(nameof(IsSelected));
            }
        }

        public long Bytes
        {
            get => _bytes;
            set
            {
                _bytes = value;
                Raise(nameof(Bytes));
                Raise(nameof(SizeText));
            }
        }

        public int FileCount
        {
            get => _files;
            set
            {
                _files = value;
                Raise(nameof(FileCount));
                Raise(nameof(CountText));
            }
        }

        public bool IsScanned
        {
            get => _scanned;
            set
            {
                _scanned = value;
                Raise(nameof(IsScanned));
                Raise(nameof(SizeText));
                Raise(nameof(CountText));
            }
        }

        public string SizeText => _scanned ? JunkCleaner.FormatBytes(_bytes) : "-";

        public string CountText => !_scanned ? "" : _files == 1 ? "1 item" : $"{_files:N0} items";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public static class JunkCleaner
    {
        // ===== Categories =====
        public static List<CleanCategory> CreateCategories()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            var all = new List<CleanCategory>
            {
                new()
                {
                    Name = "Temporary files",
                    Description = "Your temp folder. Only files untouched for over 24 hours.",
                    Paths = Distinct(Path.GetTempPath(), Path.Combine(local, "Temp")),
                    MinAge = TimeSpan.FromDays(1),
                    IsSelected = true
                },
                new()
                {
                    Name = "Windows temp files",
                    Description = "System temp folder. Some files need administrator rights and are skipped.",
                    Paths = Distinct(Path.Combine(windows, "Temp")),
                    MinAge = TimeSpan.FromDays(1),
                    IsSelected = true
                },
                new()
                {
                    Name = "Chrome cache",
                    Description = "Cached web files only. Logins and history are kept. Best with Chrome closed.",
                    Paths = Distinct(ChromiumCachePaths(Path.Combine(local, "Google", "Chrome", "User Data")).ToArray()),
                    IsSelected = true
                },
                new()
                {
                    Name = "Edge cache",
                    Description = "Cached web files only. Logins and history are kept. Best with Edge closed.",
                    Paths = Distinct(ChromiumCachePaths(Path.Combine(local, "Microsoft", "Edge", "User Data")).ToArray()),
                    IsSelected = true
                },
                new()
                {
                    Name = "Brave cache",
                    Description = "Cached web files only. Logins and history are kept. Best with Brave closed.",
                    Paths = Distinct(ChromiumCachePaths(Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")).ToArray()),
                    IsSelected = true
                },
                new()
                {
                    Name = "Firefox cache",
                    Description = "Cached web files only. Logins and history are kept. Best with Firefox closed.",
                    Paths = Distinct(FirefoxCachePaths(Path.Combine(local, "Mozilla", "Firefox", "Profiles")).ToArray()),
                    IsSelected = true
                },
                new()
                {
                    Name = "Crash dumps and error reports",
                    Description = "Memory dumps and Windows Error Reporting files from crashed programs.",
                    Paths = Distinct(
                        Path.Combine(local, "CrashDumps"),
                        Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive"),
                        Path.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue")),
                    IsSelected = true
                },
                new()
                {
                    Name = "Graphics shader cache",
                    Description = "DirectX shader cache. Games and apps rebuild it, so first launches may be a bit slower.",
                    Paths = Distinct(Path.Combine(local, "D3DSCache")),
                    IsSelected = true
                },
                new()
                {
                    Name = "NuGet download cache",
                    Description = "Cached package metadata for Visual Studio and dotnet. Re-downloaded when needed. Installed packages are untouched.",
                    Paths = Distinct(
                        Path.Combine(local, "NuGet", "v3-cache"),
                        Path.Combine(local, "NuGet", "plugins-cache")),
                    IsSelected = false
                },
                new()
                {
                    Name = "Recycle Bin",
                    Description = "Permanently deletes everything in the Recycle Bin on all drives.",
                    Kind = CleanKind.RecycleBin,
                    IsSelected = false
                }
            };

            // Hide rows for software that is not installed on this PC.
            return all.Where(c => c.Kind == CleanKind.RecycleBin || c.Paths.Any(Directory.Exists)).ToList();
        }

        private static IReadOnlyList<string> Distinct(params string[] paths)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();

            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                try
                {
                    var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
                    if (seen.Add(full)) result.Add(full);
                }
                catch { /* invalid path: ignore */ }
            }

            return result;
        }

        private static IEnumerable<string> ChromiumCachePaths(string userDataDir)
        {
            if (!Directory.Exists(userDataDir)) yield break;

            var profiles = new List<string>();
            var def = Path.Combine(userDataDir, "Default");
            if (Directory.Exists(def)) profiles.Add(def);

            try { profiles.AddRange(Directory.EnumerateDirectories(userDataDir, "Profile *")); }
            catch { /* unreadable: use what we have */ }

            foreach (var profile in profiles)
                foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache" })
                    yield return Path.Combine(profile, sub);
        }

        private static IEnumerable<string> FirefoxCachePaths(string profilesDir)
        {
            if (!Directory.Exists(profilesDir)) yield break;

            var profiles = new List<string>();
            try { profiles.AddRange(Directory.EnumerateDirectories(profilesDir)); }
            catch { /* unreadable: nothing to add */ }

            foreach (var profile in profiles)
                yield return Path.Combine(profile, "cache2");
        }

        // ===== Scan =====
        /// <summary>Measures a category. If cancelled, returns whatever was counted so far.</summary>
        public static ScanResult Measure(CleanCategory category, CancellationToken ct = default)
        {
            if (category.Kind == CleanKind.RecycleBin) return QueryRecycleBin();

            long total = 0;
            int files = 0;

            foreach (var root in category.Paths)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    foreach (var file in EnumerateOldFiles(root, category.MinAge, ct))
                    {
                        try
                        {
                            total += file.Length;
                            files++;
                        }
                        catch { /* file vanished between listing and sizing */ }
                    }
                }
                catch { /* folder vanished or is unreadable: count what we got */ }
            }

            return new ScanResult(total, files);
        }

        // ===== Clean =====
        /// <summary>Cleans a category. If cancelled, returns what was freed up to that point.</summary>
        public static ScanResult Clean(CleanCategory category, CancellationToken ct = default)
        {
            if (category.Kind == CleanKind.RecycleBin) return EmptyRecycleBin();

            long freed = 0;
            int files = 0;

            foreach (var root in category.Paths)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var r = CleanFolder(root, category.MinAge, ct);
                    freed += r.Bytes;
                    files += r.Files;
                }
                catch { }
            }

            return new ScanResult(freed, files);
        }

        private static EnumerationOptions Options() => new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Never follow junctions/symlinks: they could point outside the folder.
            // (Setting this replaces the default Hidden|System skip, so those files are included.)
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        private static IEnumerable<FileInfo> EnumerateOldFiles(string root, TimeSpan minAge, CancellationToken ct)
        {
            if (!Directory.Exists(root)) yield break;

            DateTime cutoff = DateTime.UtcNow - minAge;

            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", Options()))
            {
                if (ct.IsCancellationRequested) yield break;
                if (minAge > TimeSpan.Zero && file.LastWriteTimeUtc > cutoff) continue;
                yield return file;
            }
        }

        private static ScanResult CleanFolder(string root, TimeSpan minAge, CancellationToken ct)
        {
            if (!Directory.Exists(root)) return default;

            long freed = 0;
            int files = 0;

            // ToList first so deleting does not disturb the enumeration.
            foreach (var file in EnumerateOldFiles(root, minAge, ct).ToList())
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    long size = file.Length;
                    file.Attributes = FileAttributes.Normal;   // allow deleting read-only files
                    file.Delete();
                    freed += size;
                    files++;
                }
                catch { /* in use or no permission: skip */ }
            }

            if (ct.IsCancellationRequested) return new ScanResult(freed, files);

            // Remove folders that are now empty, deepest first. The root itself stays.
            try
            {
                DateTime cutoff = DateTime.UtcNow - minAge;
                var dirs = new DirectoryInfo(root).EnumerateDirectories("*", Options())
                    .Where(d => minAge <= TimeSpan.Zero || d.LastWriteTimeUtc <= cutoff)
                    .Select(d => d.FullName)
                    .OrderByDescending(p => p.Length)
                    .ToList();

                foreach (var dir in dirs)
                {
                    if (ct.IsCancellationRequested) break;
                    try { Directory.Delete(dir, false); }   // fails (safely) if not empty
                    catch { }
                }
            }
            catch { }

            return new ScanResult(freed, files);
        }

        // ===== Recycle Bin =====
        private static ScanResult QueryRecycleBin()
        {
            try
            {
                var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                if (SHQueryRecycleBin(null, ref info) != 0) return default;

                int count = (int)Math.Min(int.MaxValue, Math.Max(0, info.i64NumItems));
                return new ScanResult(info.i64Size, count);
            }
            catch { return default; }
        }

        private static ScanResult EmptyRecycleBin()
        {
            try
            {
                var before = QueryRecycleBin();
                const uint SHERB_NOCONFIRMATION = 0x00000001;
                const uint SHERB_NOPROGRESSUI = 0x00000002;
                const uint SHERB_NOSOUND = 0x00000004;

                int hr = SHEmptyRecycleBin(IntPtr.Zero, null,
                    SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                return hr == 0 ? before : default;
            }
            catch { return default; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

        // ===== Helpers =====
        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) bytes = 0;
            double b = bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
            return $"{b:F1} {units[i]}";
        }
    }
}
