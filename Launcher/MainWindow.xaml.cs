using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicIsland.Services;

namespace DynamicIslandLauncher
{
    public partial class MainWindow : Window
    {
        private readonly SettingsService _settingsService;
        private bool _isInstalling = false;

        public MainWindow()
        {
            try { File.AppendAllText("launcher.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow ctor start\n"); } catch { }
            try
            {
                _settingsService = new SettingsService(Dispatcher);
                InitializeComponent();
                Loaded += MainWindow_Loaded;
                Closing += (s, e) => { try { File.AppendAllText("launcher.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow Closing (Cancel={e.Cancel})\n"); } catch { } };
                Closed += (s, e) => { try { File.AppendAllText("launcher.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow Closed\n"); } catch { } };
            }
            catch (Exception ex)
            {
                try { File.AppendAllText("launcher.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow ctor EXCEPTION: {ex}\n"); } catch { }
                throw;
            }
            try { File.AppendAllText("launcher.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow ctor end\n"); } catch { }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                File.AppendAllText("launcher.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow_Loaded start\n");
                try { AcrylicHelper.EnableBlur(this); } catch (Exception ex) { File.AppendAllText("launcher.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Blur error: {ex}\n"); }
                LoadCurrentSettingsToUI();

                // Check if already installed
                string targetExe = Path.Combine(SettingsService.InstallDirectory, "DynamicIsland.exe");
                if (File.Exists(targetExe))
                {
                    InstallStatusTitle.Text = "Dynamic Island встановлено";
                    InstallStatusSubText.Text = "Остання версія з GitHub • Активна";
                    InstallActionButton.Content = "Оновити";
                }
                else
                {
                    InstallStatusTitle.Text = "Встановлення з GitHub";
                    InstallStatusSubText.Text = "Версія 2.0 (36.5 МБ) • Репозиторій nout0688-cloud";
                    InstallActionButton.Content = "Встановити →";
                }
                File.AppendAllText("launcher.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow_Loaded end\n");
            }
            catch (Exception ex)
            {
                File.AppendAllText("launcher.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow_Loaded EXCEPTION: {ex}\n");
            }
        }

        private void LoadCurrentSettingsToUI()
        {
            var s = _settingsService.Current;

            // Alignment
            if (string.Equals(s.Alignment, "Left", StringComparison.OrdinalIgnoreCase))
                AlignLeftRadio.IsChecked = true;
            else if (string.Equals(s.Alignment, "Right", StringComparison.OrdinalIgnoreCase))
                AlignRightRadio.IsChecked = true;
            else
                AlignCenterRadio.IsChecked = true;

            // Top Offset
            TopOffsetSlider.Value = Math.Clamp(s.TopOffset, 0, 50);
            TopOffsetValText.Text = $"{(int)TopOffsetSlider.Value} px";

            // Scale
            int scalePct = (int)Math.Round(s.Scale * 100);
            ScaleSlider.Value = Math.Clamp(scalePct, 80, 130);
            ScaleValText.Text = $"{scalePct}%";

            // Theme
            if (string.Equals(s.Theme, "Light", StringComparison.OrdinalIgnoreCase))
                ThemeLightRadio.IsChecked = true;
            else if (string.Equals(s.Theme, "System", StringComparison.OrdinalIgnoreCase))
                ThemeSystemRadio.IsChecked = true;
            else
                ThemeDarkRadio.IsChecked = true;

            // Auto-start
            bool auto = SettingsService.GetAutoStart();
            SettingsAutoStartCheck.IsChecked = auto;
        }

        private async void StartInstall_Click(object sender, RoutedEventArgs e)
        {
            if (_isInstalling) return;
            _isInstalling = true;

            InstallProgressGrid.Visibility = Visibility.Visible;
            InstallActionButton.IsEnabled = false;

            // Execute installation asynchronously
            await PerformInstallationAsync();

            _isInstalling = false;
        }

        private async Task PerformInstallationAsync()
        {
            string destDir = SettingsService.InstallDirectory;
            string tempZip = Path.Combine(Path.GetTempPath(), "dynamic-island-bundle.zip");

            // Step 1: Initialize directory
            await SetProgress(5, "Підготовка директорії...", "%AppData%\\DynamicIsland");
            await Task.Delay(200);
            Directory.CreateDirectory(destDir);

            // Step 2: Download from GitHub
            bool downloadSuccess = false;
            string githubUrl = "https://raw.githubusercontent.com/nout0688-cloud/dynamicFocacaca/main/dynamic-island-bundle.zip";

            await SetProgress(10, "Підключення до GitHub...", "github.com/nout0688-cloud/dynamicFocacaca");

            try
            {
                using var client = new System.Net.Http.HttpClient();
                client.Timeout = TimeSpan.FromMinutes(3);
                using var response = await client.GetAsync(githubUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                if (response.IsSuccessStatusCode)
                {
                    long totalBytes = response.Content.Headers.ContentLength ?? 36000000;
                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                    byte[] buffer = new byte[81920];
                    long totalRead = 0;
                    int read;
                    DateTime lastUpdate = DateTime.MinValue;

                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read));
                        totalRead += read;

                        if ((DateTime.Now - lastUpdate).TotalMilliseconds > 100)
                        {
                            lastUpdate = DateTime.Now;
                            double pct = 10.0 + ((double)totalRead / totalBytes) * 65.0; // 10% to 75%
                            double mbRead = totalRead / (1024.0 * 1024.0);
                            double mbTotal = totalBytes / (1024.0 * 1024.0);
                            await SetProgress(Math.Min(75, pct), "Завантаження з GitHub...", $"{mbRead:F1} МБ з {mbTotal:F1} МБ ({pct:F0}%)");
                        }
                    }

                    downloadSuccess = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitHub download error: {ex.Message}");
            }

            // Fallback: If GitHub download wasn't successful (e.g. offline before push), check local bundle or files
            if (!downloadSuccess)
            {
                string localBundle = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dynamic-island-bundle.zip");
                if (!File.Exists(localBundle))
                {
                    string parent = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
                    localBundle = Path.Combine(parent, "dynamic-island-bundle.zip");
                }

                if (File.Exists(localBundle))
                {
                    await SetProgress(70, "Використання локального пакунка...", "Локальна резервна копія");
                    File.Copy(localBundle, tempZip, true);
                    downloadSuccess = true;
                }
            }

            // Step 3: Extract bundle
            if (downloadSuccess && File.Exists(tempZip))
            {
                await SetProgress(80, "Розпакування компонентів...", "%AppData%\\DynamicIsland");
                await Task.Run(() =>
                {
                    try
                    {
                        System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, destDir, overwriteFiles: true);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Extract error: {ex.Message}");
                    }
                });
                try { File.Delete(tempZip); } catch { }
            }
            else
            {
                await SetProgress(80, "Копіювання компонентів...", "DynamicIsland.exe та бібліотеки");
                await Task.Run(() => CopyLocalFiles(destDir));
            }

            // Step 4: Configure Auto-start & settings
            await SetProgress(95, "Завершення налаштування...", "Параметри та реєстр Windows");
            bool enableAuto = SettingsAutoStartCheck.IsChecked == true;
            SettingsService.SetAutoStart(enableAuto, Path.Combine(destDir, "DynamicIsland.exe"));

            var currentSettings = _settingsService.Current;
            currentSettings.AutoStart = enableAuto;
            _settingsService.Save(currentSettings);
            await Task.Delay(300);

            // Complete!
            await SetProgress(100, "Успішно встановлено!", "Dynamic Island готовий до використання");
            InstallStatusTitle.Text = "Dynamic Island встановлено";
            InstallStatusSubText.Text = "Остання версія з GitHub • Активна на екрані";
            InstallActionButton.Content = "Запущено ✓";
            InstallActionButton.IsEnabled = true;
            await Task.Delay(1200);
            InstallProgressGrid.Visibility = Visibility.Collapsed;

            // Start island immediately
            StartOrRestartIsland();
        }

        private void CopyLocalFiles(string destDir)
        {
            string sourceDir = AppDomain.CurrentDomain.BaseDirectory;
            if (Directory.Exists(sourceDir))
            {
                foreach (var file in Directory.GetFiles(sourceDir, "*.*"))
                {
                    string name = Path.GetFileName(file);
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Copy(file, Path.Combine(destDir, name), true); } catch { }
                    }
                }
                string srcFonts = Path.Combine(sourceDir, "Fonts");
                if (Directory.Exists(srcFonts))
                {
                    string dstFonts = Path.Combine(destDir, "Fonts");
                    Directory.CreateDirectory(dstFonts);
                    foreach (var font in Directory.GetFiles(srcFonts, "*.ttf"))
                    {
                        try { File.Copy(font, Path.Combine(dstFonts, Path.GetFileName(font)), true); } catch { }
                    }
                }
            }
        }

        private async Task SetProgress(double pct, string status, string detail)
        {
            InstallStatusTitle.Text = status;
            InstallDetailText.Text = detail;
            InstallPercentText.Text = $"{(int)pct}%";

            double maxBarWidth = 430;
            double targetWidth = (pct / 100.0) * maxBarWidth;

            var anim = new DoubleAnimation(ProgressBarFill.ActualWidth, targetWidth, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            ProgressBarFill.BeginAnimation(WidthProperty, anim);
            await Task.Yield();
        }

        // ════════════════ SETTINGS HANDLERS ════════════════

        private void Alignment_Checked(object sender, RoutedEventArgs e)
        {
            if (_settingsService == null || AlignLeftRadio == null || AlignRightRadio == null) return;
            if (AlignLeftRadio.IsChecked == true)
                _settingsService.Current.Alignment = "Left";
            else if (AlignRightRadio.IsChecked == true)
                _settingsService.Current.Alignment = "Right";
            else
                _settingsService.Current.Alignment = "Center";

            _settingsService.Save(_settingsService.Current);
        }

        private void TopOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settingsService == null || TopOffsetValText == null) return;
            int val = (int)e.NewValue;
            TopOffsetValText.Text = $"{val} px";
            _settingsService.Current.TopOffset = val;
            _settingsService.Save(_settingsService.Current);
        }

        private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settingsService == null || ScaleValText == null) return;
            int pct = (int)e.NewValue;
            ScaleValText.Text = $"{pct}%";
            _settingsService.Current.Scale = pct / 100.0;
            _settingsService.Save(_settingsService.Current);
        }

        private void Theme_Checked(object sender, RoutedEventArgs e)
        {
            if (_settingsService == null || ThemeLightRadio == null || ThemeSystemRadio == null) return;
            if (ThemeLightRadio.IsChecked == true)
                _settingsService.Current.Theme = "Light";
            else if (ThemeSystemRadio.IsChecked == true)
                _settingsService.Current.Theme = "System";
            else
                _settingsService.Current.Theme = "Dark";

            _settingsService.Save(_settingsService.Current);
        }

        private void AccentColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string colorHex)
            {
                _settingsService.Current.AccentColor = colorHex;
                _settingsService.Save(_settingsService.Current);
            }
        }

        private void AutoStart_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsService == null || SettingsAutoStartCheck == null) return;
            bool enable = SettingsAutoStartCheck.IsChecked == true;
            _settingsService.Current.AutoStart = enable;
            _settingsService.Save(_settingsService.Current);

            string exePath = Path.Combine(SettingsService.InstallDirectory, "DynamicIsland.exe");
            if (!File.Exists(exePath))
                exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DynamicIsland.exe");

            SettingsService.SetAutoStart(enable, exePath);
        }

        private void RestartRow_Click(object sender, MouseButtonEventArgs e)
        {
            StartOrRestartIsland();
        }

        private void OpenFolderRow_Click(object sender, MouseButtonEventArgs e)
        {
            OpenFolder();
        }

        private void StartOrRestartIsland()
        {
            try
            {
                // Terminate previous running processes
                foreach (var p in Process.GetProcessesByName("DynamicIsland"))
                {
                    try { p.Kill(); } catch { }
                }

                // Determine target exe
                string exePath = Path.Combine(SettingsService.InstallDirectory, "DynamicIsland.exe");
                if (!File.Exists(exePath))
                    exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DynamicIsland.exe");

                if (!File.Exists(exePath))
                {
                    // Fallback to publish or parent
                    string publishExe = @"c:\Users\dolph\OneDrive\Desktop\dynamic ayland\publish\DynamicIsland.exe";
                    if (File.Exists(publishExe)) exePath = publishExe;
                }

                if (File.Exists(exePath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Помилка запуску Dynamic Island: {ex.Message}", "Помилка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OpenFolder()
        {
            try
            {
                string dir = SettingsService.InstallDirectory;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
