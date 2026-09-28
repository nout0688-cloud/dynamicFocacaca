using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DynamicIsland.Services
{
    public class IslandSettings
    {
        public string Alignment { get; set; } = "Center"; // "Left", "Center", "Right"
        public double TopOffset { get; set; } = 8.0;       // 0 to 50
        public double Scale { get; set; } = 1.0;          // 0.8 to 1.3
        public string Theme { get; set; } = "Dark";       // "Dark", "Light", "System"
        public string AccentColor { get; set; } = "#0A84FF"; // iOS Blue
        public bool AutoStart { get; set; } = true;
    }

    public class SettingsService : IDisposable
    {
        private static readonly string AppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DynamicIsland");

        public static string SettingsFilePath => Path.Combine(AppDataFolder, "settings.json");
        public static string InstallDirectory => AppDataFolder;

        public IslandSettings Current { get; private set; } = new();

        public event Action<IslandSettings>? SettingsChanged;

        private FileSystemWatcher? _watcher;
        private DispatcherTimer? _debounceTimer;
        private readonly Dispatcher _dispatcher;

        public SettingsService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            Load();
            SetupWatcher();
        }

        public void Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var loaded = JsonSerializer.Deserialize<IslandSettings>(json);
                    if (loaded != null)
                    {
                        Current = loaded;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading settings: {ex.Message}");
            }

            Current = new IslandSettings();
        }

        public void Save(IslandSettings settings)
        {
            try
            {
                Directory.CreateDirectory(AppDataFolder);
                Current = settings;
                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving settings: {ex.Message}");
            }
        }

        private void SetupWatcher()
        {
            try
            {
                Directory.CreateDirectory(AppDataFolder);

                _debounceTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(200)
                };
                _debounceTimer.Tick += (s, e) =>
                {
                    _debounceTimer.Stop();
                    Load();
                    SettingsChanged?.Invoke(Current);
                };

                _watcher = new FileSystemWatcher(AppDataFolder, "settings.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                _watcher.Changed += OnFileChanged;
                _watcher.Created += OnFileChanged;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error initializing settings watcher: {ex.Message}");
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            _dispatcher.InvokeAsync(() =>
            {
                _debounceTimer?.Stop();
                _debounceTimer?.Start();
            });
        }

        public static bool GetAutoStart()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                return key?.GetValue("DynamicIsland") != null;
            }
            catch
            {
                return false;
            }
        }

        public static void SetAutoStart(bool enable, string? targetExe = null)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) return;

                if (enable)
                {
                    string exe = targetExe ?? Path.Combine(AppDataFolder, "DynamicIsland.exe");
                    key.SetValue("DynamicIsland", $"\"{exe}\"");
                }
                else
                {
                    if (key.GetValue("DynamicIsland") != null)
                    {
                        key.DeleteValue("DynamicIsland", false);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting autostart: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _debounceTimer?.Stop();
            _watcher?.Dispose();
        }
    }
}
