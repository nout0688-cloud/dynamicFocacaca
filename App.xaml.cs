using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace DynamicIsland
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            try
            {
                File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] App OnStartup started\n");
            }
            catch { }

            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                try
                {
                    File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] AppDomain Unhandled: {ev.ExceptionObject}\n");
                }
                catch { }
            };

            DispatcherUnhandledException += (s, ev) =>
            {
                try
                {
                    File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Dispatcher Unhandled: {ev.Exception}\n");
                }
                catch { }
                ev.Handled = true;
            };

            TaskScheduler.UnobservedTaskException += (s, ev) =>
            {
                try
                {
                    File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] UnobservedTaskException: {ev.Exception}\n");
                }
                catch { }
            };

            AppDomain.CurrentDomain.ProcessExit += (s, ev) =>
            {
                try
                {
                    File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] AppDomain ProcessExit\n");
                }
                catch { }
            };

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] App OnExit: Code={e.ApplicationExitCode}\n");
            }
            catch { }
            base.OnExit(e);
        }
    }
}
