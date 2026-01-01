using System;
using System.Windows;

namespace archimedes
{
  public interface IBootTidalAction
  {
    void Boot();
  }

  public sealed class ResourceDictionaryThemeToggler : IThemeToggler
  {
    public static event EventHandler? ThemeChanged;

    public void ToggleTheme()
    {
      var app = Application.Current;
      if (app is null) return;

      var merged = app.Resources.MergedDictionaries;

      int idx = -1;
      string current = "";

      for (int i = 0; i < merged.Count; i++)
      {
        var src = merged[i].Source?.OriginalString ?? "";
        src = src.Replace('\\', '/');

        if (src.Contains("Themes/", StringComparison.OrdinalIgnoreCase) &&
            (src.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
             src.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)))
        {
          idx = i;
          current = src;
          break;
        }
      }

      if (idx < 0) return;

      bool currentlyDark = current.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase);
      var next = new Uri(currentlyDark ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative);

      merged[idx].Source = next;

      ThemeChanged?.Invoke(this, EventArgs.Empty);
    }
  }

  public sealed class PlaceholderBootTidalAction : IBootTidalAction
  {
    public void Boot()
    {
      MessageBox.Show("Boot Tidal (placeholder).", "archimedes");
    }
  }
}
