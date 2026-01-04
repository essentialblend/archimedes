// Views/TransportBar.xaml.cs
using archimedes.Services.Tidal;
using System.Windows;
using System.Windows.Controls;

namespace archimedes.Views
{
  public enum TidalBootState
  {
    Off,
    Booting,
    Ready
  }

  public partial class TransportBar : UserControl
  {
    private readonly ResourceDictionaryThemeToggler _themeToggler = new();
    public event RoutedEventHandler? BootTidalClicked;
    public Action<string>? AppendEvalLine { get; set; }
    public Action<bool>? SetBootGlow { get; set; }

    public ISuperColliderBootService? BootService { get; set; }

    public TransportBar()
    {
      InitializeComponent();
      Loaded += (_, __) => WireBootService();
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
      _themeToggler.ToggleTheme();
    }

    private void OnBootTidal(object sender, RoutedEventArgs e)
    {
      BootTidalClicked?.Invoke(this, e);
    }

    private void WireBootService()
    {
      if (BootService is null) return;

      BootService.StdoutLine += (_, line) =>
        Dispatch(() => AppendEvalLine?.Invoke(line));

      BootService.StderrLine += (_, line) =>
        Dispatch(() => AppendEvalLine?.Invoke("ERROR: " + line));

      BootService.ReadyChanged += (_, ready) =>
        Dispatch(() => SetBootGlow?.Invoke(ready));
    }

    private void Dispatch(Action action)
    {
      if (Dispatcher.CheckAccess()) action();
      else Dispatcher.BeginInvoke(action);
    }

    public void SetTidalReady(bool ready)
    {
      SetTidalState(ready ? TidalBootState.Ready : TidalBootState.Off);
    }

    public void SetTidalState(TidalBootState state)
    {
      Dispatch(() =>
      {
        BootTidalButton.Tag = state.ToString();
        BootTidalButton.ToolTip = state switch
        {
          TidalBootState.Ready => "Tidal ready (click to stop)",
          TidalBootState.Booting => "Booting Tidal...",
          _ => "Turn Tidal Cycles On"
        };
      });
    }

  }
}
