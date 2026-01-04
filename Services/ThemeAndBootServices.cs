// Services/ThemeAndBootServices.cs

using Microsoft.Win32;
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

    public void ApplyInitialTheme()
    {
      var pref = ThemeSettingsStore.Load().Preference;
      if (pref.Equals("Light", StringComparison.OrdinalIgnoreCase))
      {
        ApplyTheme(isDark: false, persist: false);
        return;
      }

      if (pref.Equals("Dark", StringComparison.OrdinalIgnoreCase))
      {
        ApplyTheme(isDark: true, persist: false);
        return;
      }

      ApplyTheme(IsSystemDarkTheme(), persist: false);
    }

    public void ToggleTheme()
    {
      var isDark = TryGetCurrentTheme(out var currentDark) && currentDark;
      ApplyTheme(!isDark, persist: true);
    }

    private static bool TryGetCurrentTheme(out bool isDark)
    {
      isDark = false;
      var app = Application.Current;
      if (app is null) return false;

      var merged = app.Resources.MergedDictionaries;
      for (int i = 0; i < merged.Count; i++)
      {
        var src = merged[i].Source?.OriginalString ?? "";
        src = src.Replace('\\', '/');

        if (src.Contains("Themes/", StringComparison.OrdinalIgnoreCase) &&
            (src.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
             src.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)))
        {
          isDark = src.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase);
          return true;
        }
      }

      return false;
    }

    private static void ApplyTheme(bool isDark, bool persist)
    {
      var app = Application.Current;
      if (app is null) return;

      var merged = app.Resources.MergedDictionaries;
      int idx = -1;
      for (int i = 0; i < merged.Count; i++)
      {
        var src = merged[i].Source?.OriginalString ?? "";
        src = src.Replace('\\', '/');
        if (src.Contains("Themes/", StringComparison.OrdinalIgnoreCase) &&
            (src.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
             src.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)))
        {
          idx = i;
          break;
        }
      }

      if (idx < 0) return;

      var next = new Uri(isDark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
      merged[idx].Source = next;

      if (persist)
      {
        var settings = ThemeSettingsStore.Load();
        settings.Preference = isDark ? "Dark" : "Light";
        settings.Save();
      }

      ThemeChanged?.Invoke(app, EventArgs.Empty);
    }

    private static bool IsSystemDarkTheme()
    {
      try
      {
        using var key = Registry.CurrentUser.OpenSubKey(
          @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var value = key?.GetValue("AppsUseLightTheme");
        if (value is int i) return i == 0;
        if (value is null) return true;
        if (value is byte[] bytes && bytes.Length >= 4)
          return BitConverter.ToInt32(bytes, 0) == 0;
      }
      catch
      {
        // Ignore registry errors and prefer dark.
      }

      return true;
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
