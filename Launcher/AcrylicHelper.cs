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

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        private const int DWMWCP_ROUND = 2;
        private const int DWMSBT_ACRYLIC = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        public static void EnableBlur(Window window)
        {
            var helper = new WindowInteropHelper(window);
            IntPtr hwnd = helper.Handle;

            // 1. Enable Immersive Dark Mode
            int darkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            // 2. Enable Rounded Corners (Windows 11)
            int roundCorner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref roundCorner, sizeof(int));

            // 3. Try Windows 11 Acrylic Backdrop
            int backdrop = DWMSBT_ACRYLIC;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

            // 4. If Windows 10 or DWM backdrop not supported, fallback to SetWindowCompositionAttribute
            if (hr != 0)
            {
                try
                {
                    var accent = new AccentPolicy
                    {
                        AccentState = 4, // ACCENT_ENABLE_ACRYLICBLURBEHIND
                        AccentFlags = 2,
                        GradientColor = unchecked((int)0x9918181A) // semi-transparent dark tint
                    };

                    int sizeOfAccent = Marshal.SizeOf(accent);
                    IntPtr accentPtr = Marshal.AllocHGlobal(sizeOfAccent);
                    Marshal.StructureToPtr(accent, accentPtr, false);

                    var data = new WindowCompositionAttributeData
                    {
                        Attribute = 19, // WCA_ACCENT_POLICY
                        Data = accentPtr,
                        SizeOfData = sizeOfAccent
                    };

                    SetWindowCompositionAttribute(hwnd, ref data);
                    Marshal.FreeHGlobal(accentPtr);
                }
                catch
                {
                }
            }
        }
    }
}
