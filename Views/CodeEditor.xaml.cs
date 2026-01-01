using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace archimedes.Views
{
  public partial class CodeEditor : UserControl
  {
    public CodeEditor()
    {
      InitializeComponent();
      Loaded += OnControlLoaded;
      Unloaded += OnControlUnloaded;
    }

    private void OnEditorLoaded(object sender, RoutedEventArgs e) => ApplyTheme();

    private void OnControlLoaded(object? sender, RoutedEventArgs e)
    {
      archimedes.ResourceDictionaryThemeToggler.ThemeChanged += OnThemeChanged;
    }

    private void OnControlUnloaded(object? sender, RoutedEventArgs e)
    {
      archimedes.ResourceDictionaryThemeToggler.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, System.EventArgs e) => ApplyTheme();

    public void ApplyTheme()
    {
      if (Application.Current?.TryFindResource("Brush.Editor.Caret") is SolidColorBrush caret)
        Editor.TextArea.Caret.CaretBrush = caret;
    }
  }
}
