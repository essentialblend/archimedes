// Services/EditorSettingsStore.cs

using System;
using System.IO;
using System.Text.Json;

namespace archimedes
{
  internal sealed class EditorSettingsStore
  {
    private const string FileName = "editor-settings.json";

    public bool WordWrap { get; set; } = true;

    public static EditorSettingsStore Load()
    {
      var path = GetPath();
      try
      {
        if (!File.Exists(path)) return new EditorSettingsStore();

        var json = File.ReadAllText(path);
        var settings = JsonSerializer.Deserialize<EditorSettingsStore>(json);
        return settings ?? new EditorSettingsStore();
      }
      catch
      {
        return new EditorSettingsStore();
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
