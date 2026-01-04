// Services/ThemeSettingsStore.cs

using System;
using System.IO;
using System.Text.Json;

namespace archimedes
{
  internal sealed class ThemeSettingsStore
  {
    private const string FileName = "theme-settings.json";

    public string Preference { get; set; } = "System";

    public static ThemeSettingsStore Load()
    {
      var path = GetPath();
      try
      {
        if (!File.Exists(path)) return new ThemeSettingsStore();

        var json = File.ReadAllText(path);
        var settings = JsonSerializer.Deserialize<ThemeSettingsStore>(json);
        return settings ?? new ThemeSettingsStore();
      }
      catch
      {
        return new ThemeSettingsStore();
      }
    }

    public void Save()
    {
      try
      {
        var path = GetPath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
          Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
          WriteIndented = true
        });
        File.WriteAllText(path, json);
      }
      catch
      {
        // Ignore persistence failures and keep defaults.
      }
    }

    private static string GetPath()
    {
      var dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "archimedes");

      return Path.Combine(dir, FileName);
    }
  }
}
