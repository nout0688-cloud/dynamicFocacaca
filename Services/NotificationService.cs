using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland.Services
{
    public enum NotificationApp
    {
        Telegram,
        Discord,
        WhatsApp,
        VK,
        Mail,
        Browser,
        Steam,
        Generic
    }

    public class NotificationItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string AppName { get; set; } = "";
        public string Sender { get; set; } = "";
        public string Message { get; set; } = "";
        public string TimeText { get; set; } = "сейчас";
        public NotificationApp App { get; set; } = NotificationApp.Generic;
    }

    public class NotificationService : IDisposable
    {
        public event Action<NotificationItem>? NotificationReceived;
        public event Action<IncomingCallInfo>? CallDetectedFromNotification;

        private UserNotificationListener? _listener;
        private System.Windows.Threading.DispatcherTimer? _pollTimer;
        private readonly DateTime _startTime = DateTime.UtcNow;
        private readonly Dictionary<string, DateTime> _recentDeduplication = new();
        private readonly HashSet<uint> _seenNotificationIds = new();

        public NotificationService()
        {
            _ = InitializeListenerAsync();
        }

        public async Task<bool> InitializeListenerAsync()
        {
            try
            {
                if (UserNotificationListener.Current != null)
                {
                    var status = await UserNotificationListener.Current.RequestAccessAsync();
                    try { System.IO.File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] UserNotificationListener access status: {status}\n"); } catch { }
                    if (status == UserNotificationListenerAccessStatus.Allowed)
                    {
                        _listener = UserNotificationListener.Current;

                        // Preload initial notification IDs so we don't spam old notifications on startup
                        // and clear any existing toast banners from screen
                        try
                        {
                            var existing = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                            if (existing != null)
                            {
                                foreach (var n in existing)
                                {
                                    if (n != null)
                                    {
                                        _seenNotificationIds.Add(n.Id);
                                        try { _listener.RemoveNotification(n.Id); } catch { }
                                    }
                                }
                            }
                        }
                        catch { }

                        // Try subscribing to NotificationChanged (succeeds on packaged apps)
                        try
                        {
                            _listener.NotificationChanged += Listener_NotificationChanged;
                        }
                        catch (Exception ex)
                        {
                            try { System.IO.File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] NotificationChanged event not supported (unpackaged app): {ex.Message}\n"); } catch { }
                        }

                        // Polling timer at 800ms (lightweight, zero CPU usage, offloaded to background task)
                        _pollTimer = new System.Windows.Threading.DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(800)
                        };
                        _pollTimer.Tick += async (s, e) => await PollNotificationsAsync();
                        _pollTimer.Start();

                        try { System.IO.File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] NotificationService initialized successfully (listener + poller active)\n"); } catch { }
                        return true;
                    }
                    else
                    {
                        Debug.WriteLine($"[NotificationService] Access not allowed: {status}");
                    }
                }
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] UserNotificationListener init error: {ex.Message}\n"); } catch { }
                Debug.WriteLine($"[NotificationService] Init error: {ex.Message}");
            }
            return false;
        }

        private async Task PollNotificationsAsync()
        {
            if (_listener == null) return;
            try
            {
                var notifs = await Task.Run(async () =>
                {
                    try
                    {
                        return await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                    }
                    catch
                    {
                        return null;
                    }
                });
                if (notifs == null) return;

                foreach (var notif in notifs)
                {
                    if (notif == null) continue;

                    // Skip if already seen
                    if (!_seenNotificationIds.Add(notif.Id))
                    {
                        continue;
                    }

                    // Skip stale notifications from before launch
                    if (notif.CreationTime.UtcDateTime < _startTime.AddSeconds(-2))
                    {
                        continue;
                    }

                    ProcessSingleNotification(notif);
                }

                if (_seenNotificationIds.Count > 500)
                {
                    _seenNotificationIds.Clear();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotificationService] Poll error: {ex.Message}");
            }
        }

        private void Listener_NotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
        {
            try
            {
                if (args.ChangeKind != UserNotificationChangedKind.Added) return;

                var notif = sender.GetNotification(args.UserNotificationId);
                if (notif == null) return;

                if (!_seenNotificationIds.Add(notif.Id))
                {
                    return;
                }

                if (notif.CreationTime.UtcDateTime < _startTime.AddSeconds(-5))
                {
                    return;
                }

                ProcessSingleNotification(notif);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotificationService] NotificationChanged error: {ex.Message}");
            }
        }

        private void ProcessSingleNotification(UserNotification notif)
        {
            try
            {
                string appName = "";
                try { appName = notif.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { }
                string appId = "";
                try { appId = notif.AppInfo?.Id ?? ""; } catch { }

                var binding = notif.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                if (binding == null) return;

                var texts = binding.GetTextElements();
                string title = "";
                string body = "";
                int idx = 0;
                foreach (var t in texts)
                {
                    string clean = t.Text?.Trim() ?? "";
                    if (string.IsNullOrEmpty(clean)) continue;

                    if (idx == 0)
                    {
                        title = clean;
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(body))
                        {
                            body = clean;
                        }
                        else
                        {
                            body += " • " + clean;
                        }
                    }
                    idx++;
                }

                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(title))
                {
                    title = !string.IsNullOrWhiteSpace(appName) ? appName : "Уведомление";
                }
                else
                {
                    title = title.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
                }

                if (string.IsNullOrWhiteSpace(body))
                {
                    body = "Новое сообщение";
                }
                else
                {
                    body = body.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
                }

                // Immediately dismiss/remove native toast from Windows so it doesn't pop up in the bottom-right corner!
                try
                {
                    _listener?.RemoveNotification(notif.Id);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[NotificationService] RemoveNotification error: {ex.Message}");
                }

                bool isTelegram = appName.Contains("Telegram", StringComparison.OrdinalIgnoreCase) || appId.Contains("Telegram", StringComparison.OrdinalIgnoreCase);
                bool isDiscord = appName.Contains("Discord", StringComparison.OrdinalIgnoreCase) || appId.Contains("Discord", StringComparison.OrdinalIgnoreCase);

                // Precise incoming call check (avoiding false positives on casual chat messages like 'позвони мне')
                bool isCall = false;
                if (isTelegram || isDiscord)
                {
                    string tLower = title.ToLowerInvariant();
                    string bLower = body.ToLowerInvariant();

                    isCall = tLower.Contains("входящий звонок")
                          || tLower.Contains("входящий видеозвонок")
                          || tLower.Contains("вхідний виклик")
                          || tLower.Contains("incoming call")
                          || tLower.Contains("incoming voice call")
                          || tLower.Contains("incoming video call")
                          || bLower.Contains("входящий звонок")
                          || bLower.Contains("входящий видеозвонок")
                          || bLower.Contains("вхідний виклик")
                          || bLower.Contains("incoming call")
                          || bLower.Contains("is calling")
                          || bLower.StartsWith("звонит");
                }

                if (isCall)
                {
                    var callApp = isTelegram ? CallApp.Telegram : CallApp.Discord;
                    string cleanCaller = CallService.ExtractCallerName(title);
                    CallDetectedFromNotification?.Invoke(new IncomingCallInfo
                    {
                        CallerName = cleanCaller,
                        App = callApp
                    });
                    return;
                }

                // Deduplicate repetitive notifications within 2.5 seconds
                string dedupKey = $"{appName}|{title}|{body}";
                DateTime now = DateTime.UtcNow;
                if (_recentDeduplication.TryGetValue(dedupKey, out var lastTime))
                {
                    if ((now - lastTime).TotalSeconds < 2.5)
                    {
                        return;
                    }
                }
                _recentDeduplication[dedupKey] = now;

                if (_recentDeduplication.Count > 100)
                {
                    _recentDeduplication.Clear();
                }

                // Classify application
                NotificationApp notifApp = ClassifyApp(appName, appId);

                if (string.IsNullOrWhiteSpace(appName))
                {
                    appName = notifApp switch
                    {
                        NotificationApp.Telegram => "Telegram",
                        NotificationApp.Discord => "Discord",
                        NotificationApp.WhatsApp => "WhatsApp",
                        NotificationApp.VK => "ВКонтакте",
                        NotificationApp.Mail => "Почта",
                        NotificationApp.Browser => "Браузер",
                        NotificationApp.Steam => "Steam",
                        _ => "Уведомление"
                    };
                }

                var item = new NotificationItem
                {
                    AppName = appName,
                    Sender = title,
                    Message = body,
                    TimeText = "сейчас",
                    App = notifApp
                };

                NotificationReceived?.Invoke(item);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotificationService] ProcessSingleNotification error: {ex.Message}");
            }
        }

        private static NotificationApp ClassifyApp(string appName, string appId)
        {
            string combined = (appName + " " + appId).ToLowerInvariant();

            if (combined.Contains("telegram")) return NotificationApp.Telegram;
            if (combined.Contains("discord")) return NotificationApp.Discord;
            if (combined.Contains("whatsapp")) return NotificationApp.WhatsApp;
            if (combined.Contains("vk") || combined.Contains("вконтакте")) return NotificationApp.VK;
            if (combined.Contains("mail") || combined.Contains("outlook") || combined.Contains("thunderbird")) return NotificationApp.Mail;
            if (combined.Contains("chrome") || combined.Contains("edge") || combined.Contains("firefox") || combined.Contains("opera") || combined.Contains("brave")) return NotificationApp.Browser;
            if (combined.Contains("steam")) return NotificationApp.Steam;

            return NotificationApp.Generic;
        }

        public void TriggerNotification(NotificationItem item)
        {
            NotificationReceived?.Invoke(item);
        }

        public void Dispose()
        {
            _pollTimer?.Stop();
            _pollTimer = null;

            if (_listener != null)
            {
                try
                {
                    _listener.NotificationChanged -= Listener_NotificationChanged;
                }
                catch { }
                _listener = null;
            }
        }
    }
}
