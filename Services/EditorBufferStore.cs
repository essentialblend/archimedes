// Services/EditorBufferStore.cs

using System;
using System.IO;
using System.Text;

namespace archimedes
{
  internal sealed class EditorBufferStore
  {
    private const string FileName = "editor-buffer.tidal";

    public string Load()
    {
      var path = GetPath();
      try
      {
        if (!File.Exists(path)) return string.Empty;
        return File.ReadAllText(path, Encoding.UTF8);
      }
      catch
      {
        return string.Empty;
      }
    }

    public void Save(string? text)
    {
      try
      {
        var path = GetPath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
          Directory.CreateDirectory(dir);

        File.WriteAllText(path, text ?? string.Empty, Encoding.UTF8);
      }
      catch
      {
        // Ignore persistence failures to avoid disrupting editing.
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
