using System.Windows;

namespace archimedes
{
  public partial class MainWindow : Window
  {
    private readonly ResourceDictionaryThemeToggler _themeToggler = new();

    public MainWindow()
    {
      InitializeComponent();
    }

    private void OnBootTidal(object sender, RoutedEventArgs e)
    {
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
      _themeToggler.ToggleTheme();
    }
  }
}
