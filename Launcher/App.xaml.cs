using System;
using System.IO;
using System.Windows;

namespace DynamicIslandLauncher
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            try
            {
                File.AppendAllText("launcher.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Launcher OnStartup\n");
            }
            catch { }

            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                try
                {
                    File.AppendAllText("launcher.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] UnhandledException: {ev.ExceptionObject}\n");
                }
                catch { }
            };

            DispatcherUnhandledException += (s, ev) =>
            {
                try
                {
                    File.AppendAllText("launcher.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DispatcherUnhandledException: {ev.Exception}\n");
                }
                catch { }
                ev.Handled = true;
            };

            Exit += (s, ev) =>
            {
                try
                {
                    File.AppendAllText("launcher.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] App Exit: code={ev.ApplicationExitCode}\n");
                }
                catch { }
            };

            base.OnStartup(e);
        }
    }
}
