using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace archimedes
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private static readonly object CrashGate = new();

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            new ResourceDictionaryThemeToggler().ApplyInitialTheme();
            base.OnStartup(e);
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var logDir = Path.Combine(baseDir, "archimedes", "crash");
                Directory.CreateDirectory(logDir);
                var path = Path.Combine(logDir, $"ui_crash_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                lock (CrashGate)
                {
                    File.WriteAllText(path, e.Exception.ToString());
                }
            }
            catch
            {
                // best effort only
            }
        }
    }
}
