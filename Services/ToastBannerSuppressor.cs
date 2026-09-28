using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;

namespace DynamicIsland.Services
{
    /// <summary>
    /// Suppresses native Windows bottom-right toast notification banners while Dynamic Island is running.
    /// Notifications are instead displayed inside Dynamic Island at the top of the screen.
    /// On exit, all original Windows notification settings are cleanly restored.
    /// </summary>
    public class ToastBannerSuppressor : IDisposable
    {
        private const string NOTIFICATION_SETTINGS_KEY = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
        private const string GLOBAL_TOASTS_ENABLED_VALUE = "NOC_GLOBAL_SETTING_TOASTS_ENABLED";
        private const string SHOW_BANNER_VALUE = "ShowBanner";

        private readonly Dictionary<string, int?> _originalBannerSettings = new(StringComparer.OrdinalIgnoreCase);
        private int? _originalGlobalToastsEnabled;
        private bool _isOriginalGlobalRecorded = false;
        private bool _isSuppressionActive = false;
        private readonly object _lock = new();

        public ToastBannerSuppressor()
        {
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Restore();
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Restore();
            SystemEvents.SessionEnding += (s, e) => Restore();
        }

        public void EnableSuppression()
        {
            lock (_lock)
            {
                if (_isSuppressionActive) return;
                _isSuppressionActive = true;

                try
                {
                    SuppressGlobalAndApps();
                    try { System.IO.File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ToastBannerSuppressor enabled successfully\n"); } catch { }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ToastBannerSuppressor] Enable error: {ex.Message}");
                    try { System.IO.File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ToastBannerSuppressor Enable error: {ex.Message}\n"); } catch { }
                }
            }
        }

        private void SuppressGlobalAndApps()
        {
            try
            {
                using var rootKey = Registry.CurrentUser.OpenSubKey(NOTIFICATION_SETTINGS_KEY, writable: true);
                if (rootKey == null) return;

                // 1. Global Do-Not-Disturb / Toasts suppression
                if (!_isOriginalGlobalRecorded)
                {
                    var globalVal = rootKey.GetValue(GLOBAL_TOASTS_ENABLED_VALUE);
                    if (globalVal is int intVal)
                    {
                        _originalGlobalToastsEnabled = intVal;
                    }
                    else
                    {
                        _originalGlobalToastsEnabled = null;
                    }
                    _isOriginalGlobalRecorded = true;
                }

                // Suppress global toast banners (0 = Focus Assist / suppressed banners)
                rootKey.SetValue(GLOBAL_TOASTS_ENABLED_VALUE, 0, RegistryValueKind.DWord);

                // 2. Per-app banner suppression
                var subKeyNames = rootKey.GetSubKeyNames();
                foreach (var appName in subKeyNames)
                {
                    try
                    {
                        using var appKey = rootKey.OpenSubKey(appName, writable: true);
                        if (appKey == null) continue;

                        if (!_originalBannerSettings.ContainsKey(appName))
                        {
                            var existingVal = appKey.GetValue(SHOW_BANNER_VALUE);
                            if (existingVal is int bVal)
                            {
                                _originalBannerSettings[appName] = bVal;
                            }
                            else
                            {
                                _originalBannerSettings[appName] = null;
                            }
                        }

                        // Ensure ShowBanner is 0
                        var current = appKey.GetValue(SHOW_BANNER_VALUE);
                        if (current == null || (current is int v && v != 0))
                        {
                            appKey.SetValue(SHOW_BANNER_VALUE, 0, RegistryValueKind.DWord);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ToastBannerSuppressor] SuppressGlobalAndApps error: {ex.Message}");
            }
        }

        public void Restore()
        {
            lock (_lock)
            {
                if (!_isSuppressionActive) return;
                _isSuppressionActive = false;

                try
                {
                    using var rootKey = Registry.CurrentUser.OpenSubKey(NOTIFICATION_SETTINGS_KEY, writable: true);
                    if (rootKey != null)
                    {
                        // 1. Restore global setting
                        if (_isOriginalGlobalRecorded)
                        {
                            if (_originalGlobalToastsEnabled.HasValue)
                            {
                                rootKey.SetValue(GLOBAL_TOASTS_ENABLED_VALUE, _originalGlobalToastsEnabled.Value, RegistryValueKind.DWord);
                            }
                            else
                            {
                                try { rootKey.DeleteValue(GLOBAL_TOASTS_ENABLED_VALUE, throwOnMissingValue: false); } catch { }
                            }
                        }

                        // 2. Restore per-app banner settings
                        foreach (var kvp in _originalBannerSettings)
                        {
                            try
                            {
                                using var appKey = rootKey.OpenSubKey(kvp.Key, writable: true);
                                if (appKey == null) continue;

                                if (kvp.Value.HasValue)
                                {
                                    appKey.SetValue(SHOW_BANNER_VALUE, kvp.Value.Value, RegistryValueKind.DWord);
                                }
                                else
                                {
                                    try { appKey.DeleteValue(SHOW_BANNER_VALUE, throwOnMissingValue: false); } catch { }
                                }
                            }
                            catch { }
                        }
                    }

                    _originalBannerSettings.Clear();
                    try { System.IO.File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ToastBannerSuppressor restored original notification settings\n"); } catch { }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ToastBannerSuppressor] Restore error: {ex.Message}");
                    try { System.IO.File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ToastBannerSuppressor Restore error: {ex.Message}\n"); } catch { }
                }
            }
        }

        public void Dispose()
        {
            Restore();
            GC.SuppressFinalize(this);
        }
    }
}
