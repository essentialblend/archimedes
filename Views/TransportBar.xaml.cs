// Views/TransportBar.xaml.cs
using System.Windows;
using System.Windows.Controls;

namespace archimedes.Views
{
  public partial class TransportBar : UserControl
  {
    private readonly ResourceDictionaryThemeToggler _themeToggler = new();
    private readonly PlaceholderBootTidalAction _bootTidal = new();

    public TransportBar()
    {
      InitializeComponent();
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
      _themeToggler.ToggleTheme();
    }

    private void OnBootTidal(object sender, RoutedEventArgs e)
    {
      _bootTidal.Boot();
    }
  }
}
