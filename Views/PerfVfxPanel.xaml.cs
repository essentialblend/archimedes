// Views/PerfVfxPanel.xaml.cs

using System.Windows;
using System.Windows.Controls;

namespace archimedes.Views
{
  public partial class PerfVfxPanel : UserControl
  {
    private const double AspectRatio = 9.0 / 16.0;

    public PerfVfxPanel()
    {
      InitializeComponent();
      Loaded += OnPanelLoaded;
      SizeChanged += OnPanelSizeChanged;
    }

    public void OnTidalPulse(string ident, int startLine, int startColumn, double delta)
    {
    }

    private void OnPanelLoaded(object? sender, RoutedEventArgs e)
    {
      UpdateAspectHeight();
    }

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
      if (e.WidthChanged)
        UpdateAspectHeight();
    }

    private void UpdateAspectHeight()
    {
      if (ActualWidth <= 0) return;
      var target = ActualWidth * AspectRatio;
      if (double.IsNaN(target) || target <= 0) return;
      if (double.IsNaN(Height) || System.Math.Abs(Height - target) > 0.5)
        Height = target;
    }
  }
}
