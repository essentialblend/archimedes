using System.Windows;

namespace archimedes
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            new ResourceDictionaryThemeToggler().ApplyInitialTheme();
            base.OnStartup(e);
        }
    }
}
