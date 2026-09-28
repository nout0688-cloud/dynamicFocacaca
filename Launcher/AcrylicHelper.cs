using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DynamicIslandLauncher
{
    public static class AcrylicHelper
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        public static void EnableBlur(Window window)
        {
            try
            {
                var helper = new WindowInteropHelper(window);
                IntPtr hwnd = helper.Handle;
                if (hwnd == IntPtr.Zero) return;

                // 1. Enable Immersive Dark Mode for window frame/shadow
                int darkMode = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                // 2. Enable Rounded Corners (Windows 11)
                int roundCorner = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref roundCorner, sizeof(int));
            }
            catch { }
        }
    }
}
