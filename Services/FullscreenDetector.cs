using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Timers;

namespace DynamicIsland.Services
{
    public class FullscreenDetector : IDisposable
    {
        private enum QUERY_USER_NOTIFICATION_STATE
        {
            QUNS_NOT_PRESENT = 1,
            QUNS_BUSY = 2,
            QUNS_RUNNING_D3D_FULL_SCREEN = 3,
            QUNS_PRESENTATION_MODE = 4,
            QUNS_ACCEPTS_NOTIFICATIONS = 5,
            QUNS_QUIET_TIME = 6,
            QUNS_APP = 7
        }

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE pquns);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        private static readonly HashSet<string> IgnoredSystemClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "Progman",
            "WorkerW",
            "Shell_TrayWnd",
            "Shell_SecondaryTrayWnd",
            "TaskSwitcherWnd",
            "MultitaskingViewFrame",
            "Windows.UI.Core.CoreWindow",
            "XamlExplorerHostIslandWindow",
            "ApplicationFrameWindow",
            "TopLevelWindowForOverflowXamlIsland",
            "NotifyIconOverflowWindow"
        };

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        private IntPtr _hook;
        private readonly WinEventDelegate _delegate;
        private readonly System.Timers.Timer _debounceTimer;
        private readonly uint _currentProcessId;
        private bool _isFullscreen;

        public event Action<bool>? FullscreenChanged;

        public bool IsFullscreen => _isFullscreen;

        public FullscreenDetector()
        {
            _currentProcessId = (uint)Process.GetCurrentProcess().Id;

            _debounceTimer = new System.Timers.Timer(150);
            _debounceTimer.AutoReset = false;
            _debounceTimer.Elapsed += (s, e) => CheckFullscreen();

            _delegate = new WinEventDelegate(OnForegroundWindowChanged);
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _delegate, 0, 0, WINEVENT_OUTOFCONTEXT);
            
            CheckFullscreen();
        }

        private void OnForegroundWindowChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            // Debounce to ignore rapid transient focus changes
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        public void CheckFullscreen()
        {
            bool fullscreen = DetectFullscreen();
            if (fullscreen != _isFullscreen)
            {
                _isFullscreen = fullscreen;
                FullscreenChanged?.Invoke(_isFullscreen);
            }
        }

        private bool DetectFullscreen()
        {
            // 1. Direct D3D Fullscreen check (DirectX Games)
            if (SHQueryUserNotificationState(out var state) == 0)
            {
                if (state == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN)
                {
                    return true;
                }
            }

            // 2. Active Window check
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == GetDesktopWindow() || foreground == GetShellWindow())
            {
                return false;
            }

            // Don't consider our own app as a fullscreen game
            GetWindowThreadProcessId(foreground, out uint procId);
            if (procId == _currentProcessId)
            {
                return false;
            }

            // If window is a standard maximized window (Chrome, Discord, Telegram, Explorer, etc.) -> NOT A GAME!
            if (IsZoomed(foreground))
            {
                return false;
            }

            StringBuilder className = new StringBuilder(256);
            GetClassName(foreground, className, 256);
            string cls = className.ToString();

            // Ignore Alt+Tab, Task View, Start Menu, Desktop, Taskbar
            if (IgnoredSystemClasses.Contains(cls))
            {
                return false;
            }

            // 3. Borderless fullscreen game check (unmaximized window covering the whole monitor and taskbar)
            if (GetWindowRect(foreground, out RECT rect))
            {
                IntPtr hMonitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));

                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    // Check if window completely covers the full monitor
                    bool coversMonitor = rect.Left <= mi.rcMonitor.Left &&
                                         rect.Top <= mi.rcMonitor.Top &&
                                         rect.Right >= mi.rcMonitor.Right &&
                                         rect.Bottom >= mi.rcMonitor.Bottom;

                    if (coversMonitor)
                    {
                        // Check if the Windows Taskbar is occluded
                        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
                        if (taskbar != IntPtr.Zero && GetWindowRect(taskbar, out RECT tbRect))
                        {
                            // In a true borderless game, the window covers the monitor AND covers the taskbar rect
                            if (rect.Bottom >= tbRect.Bottom && rect.Top <= tbRect.Top && rect.Left <= tbRect.Left && rect.Right >= tbRect.Right)
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        public void Dispose()
        {
            _debounceTimer.Stop();
            _debounceTimer.Dispose();

            if (_hook != IntPtr.Zero)
            {
                UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }
}
