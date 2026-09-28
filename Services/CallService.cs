using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland.Services
{
    public enum CallApp
    {
        Telegram,
        Discord,
        Unknown
    }

    public class IncomingCallInfo
    {
        public string CallerName { get; set; } = "Контакт";
        public CallApp App { get; set; } = CallApp.Telegram;
        public string Subtitle => App == CallApp.Telegram
            ? "Входящий вызов через Telegram • • • "
            : "Входящий вызов через Discord • • • ";
        public string? AvatarUri { get; set; }
    }

    public class CallService : IDisposable
    {
        public event Action<IncomingCallInfo>? IncomingCallReceived;
        public event Action? ActiveCallStarted;
        public event Action? CallEnded;

        private readonly DispatcherTimer _windowPollTimer = new();
        private readonly DispatcherTimer _callTimeoutTimer = new();
        private bool _isCallActive = false;
        private bool _isTestCall = false;
        private bool _isInActiveConversation = false;
        private bool _callDetectedFromWindow = false;
        private int _windowMissCount = 0;
        private string? _lastDetectedTitle;

        // Active call window handles
        private IntPtr _telegramCallHwnd = IntPtr.Zero;
        private IntPtr _discordCallHwnd = IntPtr.Zero;
        private bool _isTelegramCallHidden = false;

        private bool _isTestMuted = false;

        public bool IsCallActive => _isCallActive;
        public bool IsInActiveConversation => _isInActiveConversation;

        public CallService()
        {
            // 100ms interval for near-instant window interception (<1 frame)
            _windowPollTimer.Interval = TimeSpan.FromMilliseconds(100);
            _windowPollTimer.Tick += WindowPollTimer_Tick;
            _windowPollTimer.Start();

            _callTimeoutTimer.Tick += (s, e) =>
            {
                _callTimeoutTimer.Stop();
                EndCall();
            };
        }

        private void WindowPollTimer_Tick(object? sender, EventArgs e)
        {
            CheckCallWindows();
        }

        private IntPtr FindTelegramCallHwnd()
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hWnd, lParam) =>
            {
                if (IsTelegramCallWindow(hWnd, out _))
                {
                    found = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private void CheckCallWindows()
        {
            // 1. If currently in a call with Telegram:
            if (_isCallActive && _telegramCallHwnd != IntPtr.Zero)
            {
                if (!IsWindow(_telegramCallHwnd))
                {
                    if (_isInActiveConversation)
                    {
                        // Check if Telegram replaced the incoming prompt with the active call window
                        IntPtr newHwnd = FindTelegramCallHwnd();
                        if (newHwnd != IntPtr.Zero)
                        {
                            _telegramCallHwnd = newHwnd;
                            HideTelegramCallWindow(newHwnd);
                            _windowMissCount = 0;
                            return;
                        }
                    }

                    _windowMissCount++;
                    if (_windowMissCount >= 3)
                    {
                        _telegramCallHwnd = IntPtr.Zero;
                        _isTelegramCallHidden = false;
                        _isInActiveConversation = false;
                        EndCall();
                    }
                    return;
                }

                _windowMissCount = 0;

                // If ringing (not yet marked active), check if call was answered inside Telegram directly:
                if (!_isInActiveConversation)
                {
                    bool hasAccept = FindButton(_telegramCallHwnd, new[] { "Accept", "Принять", "Прийняти", "Answer", "Ответить" }) != null;
                    bool hasHangup = FindButton(_telegramCallHwnd, new[] { "End call", "Завершить", "Завершити", "Hangup" }) != null;
                    if (!hasAccept && hasHangup)
                    {
                        _isInActiveConversation = true;
                        _callTimeoutTimer.Stop();
                        ActiveCallStarted?.Invoke();
                    }
                }

                return;
            }

            if (_isCallActive && _isTestCall)
            {
                return;
            }

            // 2. Scan for incoming call windows
            bool foundCallWindow = false;
            string detectedTitle = "";
            CallApp detectedApp = CallApp.Telegram;
            IntPtr targetHwnd = IntPtr.Zero;

            EnumWindows((hWnd, lParam) =>
            {
                var sbClass = new StringBuilder(128);
                GetClassName(hWnd, sbClass, sbClass.Capacity);
                string cls = sbClass.ToString();

                bool isQt = cls.StartsWith("Qt", StringComparison.OrdinalIgnoreCase) || cls.Contains("QWindow", StringComparison.OrdinalIgnoreCase);
                bool isDiscordClass = cls.Contains("Chrome", StringComparison.OrdinalIgnoreCase) || cls.Contains("Discord", StringComparison.OrdinalIgnoreCase);

                if (!isQt && !isDiscordClass) return true; // Instant skip for 99% of windows

                GetWindowThreadProcessId(hWnd, out uint pid);
                try
                {
                    var proc = Process.GetProcessById((int)pid);
                    string procName = proc.ProcessName.ToLowerInvariant();

                    if (isQt && procName.Contains("telegram"))
                    {
                        if (IsTelegramCallWindow(hWnd, out string callerName))
                        {
                            foundCallWindow = true;
                            detectedApp = CallApp.Telegram;
                            detectedTitle = callerName;
                            targetHwnd = hWnd;
                            return false; // Stop enumeration
                        }
                    }
                    else if (isDiscordClass && procName.Contains("discord"))
                    {
                        if (IsDiscordCallWindow(hWnd, out string discordCaller))
                        {
                            foundCallWindow = true;
                            detectedApp = CallApp.Discord;
                            detectedTitle = discordCaller;
                            targetHwnd = hWnd;
                            return false;
                        }
                    }
                }
                catch { }

                return true;
            }, IntPtr.Zero);

            if (foundCallWindow && targetHwnd != IntPtr.Zero)
            {
                _callDetectedFromWindow = true;
                _windowMissCount = 0;

                if (detectedApp == CallApp.Telegram)
                {
                    _telegramCallHwnd = targetHwnd;
                    HideTelegramCallWindow(targetHwnd);
                    _isTelegramCallHidden = true;
                }
                else if (detectedApp == CallApp.Discord)
                {
                    _discordCallHwnd = targetHwnd;
                }

                if (!_isCallActive && !_isInActiveConversation)
                {
                    _lastDetectedTitle = detectedTitle;
                    _isCallActive = true;
                    _isTestCall = false;
                    _isInActiveConversation = false;

                    _callTimeoutTimer.Stop();
                    _callTimeoutTimer.Interval = TimeSpan.FromSeconds(60);
                    _callTimeoutTimer.Start();

                    IncomingCallReceived?.Invoke(new IncomingCallInfo
                    {
                        CallerName = detectedTitle,
                        App = detectedApp
                    });
                }
            }
            else
            {
                // Auto-end call if it was tracked via window and window disappeared
                if (_isCallActive && !_isTestCall && _callDetectedFromWindow && !_isInActiveConversation)
                {
                    _windowMissCount++;
                    if (_windowMissCount >= 3)
                    {
                        EndCall();
                    }
                }
            }
        }

        private void HideTelegramCallWindow(IntPtr hWnd)
        {
            try
            {
                // Make transparent and pass-through while keeping mapped for UIA
                IntPtr currentExStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
                long newExStyle = currentExStyle.ToInt64() | (long)WS_EX_LAYERED | (long)WS_EX_TOOLWINDOW | (long)WS_EX_TRANSPARENT;
                SetWindowLongPtr(hWnd, GWL_EXSTYLE, (IntPtr)newExStyle);

                // Alpha = 0 (100% transparent to human eye)
                SetLayeredWindowAttributes(hWnd, 0, 0, LWA_ALPHA);

                // Position offscreen (-2500, -2500) - avoids standard Windows minimized coords (-32000, -32000)
                SetWindowPos(hWnd, IntPtr.Zero, -2500, -2500, 0, 0,
                    SWP_NOACTIVATE | SWP_NOSIZE | SWP_NOZORDER);

                // Ensure it is visible in Windows OS subsystem so UIA tree remains responsive
                ShowWindow(hWnd, SW_SHOWNOACTIVATE);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CallService] Hide window error: {ex.Message}");
            }
        }

        private bool IsTelegramCallWindow(IntPtr hWnd, out string callerName)
        {
            callerName = "Контакт";

            if (!IsWindow(hWnd)) return false;

            // Check Win32 Class Name
            var sbClass = new StringBuilder(256);
            GetClassName(hWnd, sbClass, sbClass.Capacity);
            string cls = sbClass.ToString();

            // Ignore auxiliary windows
            if (cls.Contains("Shadow", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("Tray", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("IME", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("GDI", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("MediaControls", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Must be a Qt window
            if (!cls.StartsWith("Qt", StringComparison.OrdinalIgnoreCase) &&
                !cls.Contains("QWindow", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Check Window Rect
            if (!GetWindowRect(hWnd, out RECT r)) return false;
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;

            // Telegram Call panel is a compact popup: width ~ 260-450, height ~ 350-650
            if (w < 180 || w > 650 || h < 220 || h > 750)
            {
                return false;
            }

            // Check Window Title
            int len = GetWindowTextLength(hWnd);
            var sbTitle = new StringBuilder(len + 1);
            GetWindowText(hWnd, sbTitle, sbTitle.Capacity);
            string title = sbTitle.ToString().Trim();

            if (string.IsNullOrEmpty(title) ||
                title.Equals("TelegramDesktop", StringComparison.OrdinalIgnoreCase) ||
                title.Equals("Telegram", StringComparison.OrdinalIgnoreCase) ||
                title.StartsWith("Telegram (", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Просмотр медиа", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Media viewer", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Verify via UI Automation: Telegram main window has UIA class 'class MainWindow'
            try
            {
                var elem = AutomationElement.FromHandle(hWnd);
                if (elem != null)
                {
                    string uiaClass = elem.Current.ClassName;
                    if (uiaClass.Equals("class MainWindow", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }
            catch { }

            callerName = ExtractCallerName(title);
            return true;
        }

        private bool IsDiscordCallWindow(IntPtr hWnd, out string callerName)
        {
            callerName = "Контакт";

            if (!IsWindow(hWnd) || !IsWindowVisible(hWnd)) return false;

            int len = GetWindowTextLength(hWnd);
            if (len == 0) return false;

            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();

            if (title.Contains("Incoming Call", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Входящий звонок", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Вхідний виклик", StringComparison.OrdinalIgnoreCase))
            {
                callerName = ExtractCallerName(title);
                return true;
            }

            return false;
        }

        public static string ExtractCallerName(string title)
        {
            string clean = title
                .Replace("Входящий звонок от", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Входящий звонок:", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Входящий звонок", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Вхідний виклик від", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Вхідний виклик:", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Вхідний виклик", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Дзвінок від", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Дзвінок:", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Incoming call from", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Incoming call:", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Incoming call", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Call with", "", StringComparison.OrdinalIgnoreCase)
                .Trim();

            clean = clean.Trim(' ', ':', '-', '•', '–', '—');

            return string.IsNullOrWhiteSpace(clean) ? "Контакт" : clean;
        }

        public void TriggerCall(IncomingCallInfo info, bool isTest = false)
        {
            _isCallActive = true;
            _isTestCall = isTest;
            _isTestMuted = false;
            _isInActiveConversation = false;
            _callDetectedFromWindow = false;
            _windowMissCount = 0;

            _callTimeoutTimer.Stop();
            _callTimeoutTimer.Interval = TimeSpan.FromSeconds(30);
            _callTimeoutTimer.Start();

            IncomingCallReceived?.Invoke(info);
        }

        public void EndCall()
        {
            if (_isCallActive)
            {
                _isCallActive = false;
                _isTestCall = false;
                _isInActiveConversation = false;
                _callDetectedFromWindow = false;
                _windowMissCount = 0;
                _lastDetectedTitle = null;
                _callTimeoutTimer.Stop();
                CallEnded?.Invoke();
            }
        }

        public async Task<bool> AnswerCallAsync(CallApp app)
        {
            _isInActiveConversation = true;
            _callTimeoutTimer.Stop();
            _windowMissCount = 0;

            if (app == CallApp.Telegram && _telegramCallHwnd != IntPtr.Zero && IsWindow(_telegramCallHwnd))
            {
                // Invoke Accept button via UI Automation
                bool clicked = TryInvokeButton(_telegramCallHwnd, new[] {
                    "Accept", "Принять", "Прийняти", "Answer", "Ответить", "Відповісти"
                });

                if (!clicked)
                {
                    // Fallback: send Enter key to Telegram call window
                    PostMessage(_telegramCallHwnd, WM_KEYDOWN, (IntPtr)0x0D, IntPtr.Zero);
                    PostMessage(_telegramCallHwnd, WM_KEYUP, (IntPtr)0x0D, IntPtr.Zero);
                }

                return true;
            }
            else if (app == CallApp.Discord)
            {
                return true;
            }

            if (_isTestCall)
            {
                return true;
            }

            await Task.CompletedTask;
            return false;
        }

        public async Task DeclineCallAsync(CallApp app)
        {
            if (app == CallApp.Telegram && _telegramCallHwnd != IntPtr.Zero && IsWindow(_telegramCallHwnd))
            {
                try
                {
                    // 1. Try invoking Decline button
                    bool invoked = TryInvokeButton(_telegramCallHwnd, new[] {
                        "Decline", "Отклонить", "Відхилити", "Cancel", "Отмена", "Скасувати"
                    });

                    // 2. Also send Escape key
                    PostMessage(_telegramCallHwnd, WM_KEYDOWN, (IntPtr)0x1B, IntPtr.Zero);
                    PostMessage(_telegramCallHwnd, WM_KEYUP, (IntPtr)0x1B, IntPtr.Zero);

                    // 3. Post WM_CLOSE
                    PostMessage(_telegramCallHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CallService] DeclineCall error: {ex.Message}");
                }

                _telegramCallHwnd = IntPtr.Zero;
                _isTelegramCallHidden = false;
                _isInActiveConversation = false;
            }
            else if (app == CallApp.Discord && _discordCallHwnd != IntPtr.Zero && IsWindow(_discordCallHwnd))
            {
                PostMessage(_discordCallHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                _discordCallHwnd = IntPtr.Zero;
            }

            EndCall();
            await Task.CompletedTask;
        }

        public async Task HangUpCallAsync(CallApp app)
        {
            if (app == CallApp.Telegram && _telegramCallHwnd != IntPtr.Zero && IsWindow(_telegramCallHwnd))
            {
                try
                {
                    // 1. Try invoking End Call button
                    bool invoked = TryInvokeButton(_telegramCallHwnd, new[] {
                        "End call", "Завершить звонок", "Завершить", "Завершити виклик", "Завершити", "Hangup"
                    });

                    // 2. Also send Escape key
                    PostMessage(_telegramCallHwnd, WM_KEYDOWN, (IntPtr)0x1B, IntPtr.Zero);
                    PostMessage(_telegramCallHwnd, WM_KEYUP, (IntPtr)0x1B, IntPtr.Zero);

                    // 3. Post WM_CLOSE
                    PostMessage(_telegramCallHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CallService] HangUpCall error: {ex.Message}");
                }

                _telegramCallHwnd = IntPtr.Zero;
                _isTelegramCallHidden = false;
                _isInActiveConversation = false;
            }
            else if (app == CallApp.Discord && _discordCallHwnd != IntPtr.Zero && IsWindow(_discordCallHwnd))
            {
                PostMessage(_discordCallHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                _discordCallHwnd = IntPtr.Zero;
            }

            EndCall();
            await Task.CompletedTask;
        }

        public bool IsMicrophoneMuted()
        {
            if (_telegramCallHwnd == IntPtr.Zero || !IsWindow(_telegramCallHwnd))
                return false;

            // Telegram sets accessible name to "Unmute microphone" (Включить микрофон / Увімкнути мікрофон) when MUTED
            var btn = FindButton(_telegramCallHwnd, new[] { "Unmute", "Включить микрофон", "Увімкнути мікрофон" });
            return btn != null;
        }

        public async Task<bool> ToggleMuteAsync()
        {
            if (_telegramCallHwnd != IntPtr.Zero && IsWindow(_telegramCallHwnd))
            {
                TryInvokeButton(_telegramCallHwnd, new[] {
                    "Mute", "Выключить микрофон", "Вимкнути мікрофон",
                    "Unmute", "Включить микрофон", "Увімкнути мікрофон"
                });

                await Task.Delay(100);
                return IsMicrophoneMuted();
            }
            else if (_isTestCall)
            {
                _isTestMuted = !_isTestMuted;
                return _isTestMuted;
            }
            return false;
        }

        private static AutomationElement? FindButton(IntPtr hWnd, string[] names)
        {
            try
            {
                if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return null;

                var element = AutomationElement.FromHandle(hWnd);
                if (element == null) return null;

                var buttons = element.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

                foreach (AutomationElement btn in buttons)
                {
                    try
                    {
                        string name = btn.Current.Name;
                        foreach (var target in names)
                        {
                            if (name.Contains(target, StringComparison.OrdinalIgnoreCase))
                            {
                                return btn;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CallService] FindButton error: {ex.Message}");
            }
            return null;
        }

        private static bool TryInvokeButton(IntPtr hWnd, string[] names)
        {
            var btn = FindButton(hWnd, names);
            if (btn != null)
            {
                try
                {
                    if (btn.TryGetCurrentPattern(InvokePattern.Pattern, out object patternObj))
                    {
                        ((InvokePattern)patternObj).Invoke();
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CallService] Invoke error: {ex.Message}");
                }

                // Fallback: click at button bounding rect center
                try
                {
                    var rect = btn.Current.BoundingRectangle;
                    if (rect.Width > 0 && rect.Height > 0 && GetWindowRect(hWnd, out RECT winRect))
                    {
                        int clientX = (int)(rect.Left + rect.Width / 2 - winRect.Left);
                        int clientY = (int)(rect.Top + rect.Height / 2 - winRect.Top);
                        IntPtr lParam = (IntPtr)((clientY << 16) | (clientX & 0xFFFF));
                        PostMessage(hWnd, WM_LBUTTONDOWN, (IntPtr)1, lParam);
                        PostMessage(hWnd, WM_LBUTTONUP, IntPtr.Zero, lParam);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        public void Dispose()
        {
            _callTimeoutTimer.Stop();
            _windowPollTimer.Stop();

            if (_telegramCallHwnd != IntPtr.Zero && IsWindow(_telegramCallHwnd) && _isTelegramCallHidden)
            {
                try
                {
                    SetLayeredWindowAttributes(_telegramCallHwnd, 0, 255, LWA_ALPHA);
                    SetWindowPos(_telegramCallHwnd, IntPtr.Zero, 100, 100, 360, 500, SWP_SHOWWINDOW);
                    ShowWindow(_telegramCallHwnd, SW_SHOW);
                }
                catch { }
            }
        }

        // Win32 Interop
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private const int GWL_EXSTYLE = -20;
        private const uint WS_EX_LAYERED = 0x00080000;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_EX_TRANSPARENT = 0x00000020;
        private const uint LWA_ALPHA = 0x00000002;

        private const int SW_SHOW = 5;
        private const int SW_SHOWNOACTIVATE = 4;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private const uint WM_CLOSE = 0x0010;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : (IntPtr)GetWindowLong32(hWnd, nIndex);
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            return IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : (IntPtr)SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32());
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    }
}
