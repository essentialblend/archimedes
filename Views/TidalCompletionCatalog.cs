// Views/TidalCompletionCatalog.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace archimedes.Views
{
  internal sealed record TidalCompletionItem(string Name, string Kind, string Module, string Detail, string Doc)
  {
    public string DocOrDetail => string.IsNullOrWhiteSpace(Doc) ? Detail : Doc;
    public string Signature => string.IsNullOrWhiteSpace(Doc) ? string.Empty : Detail;
  }

  internal static class TidalCompletionCatalog
  {
    private static IReadOnlyList<TidalCompletionItem>? _items;

    public static IReadOnlyList<TidalCompletionItem> Items => _items ??= Load();

    private static IReadOnlyList<TidalCompletionItem> Load()
    {
      var info = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/TidalCompletion.txt", UriKind.Absolute))
        ?? Application.GetResourceStream(new Uri("pack://application:,,,/archimedes;component/Resources/TidalCompletion.txt", UriKind.Absolute))
        ?? throw new InvalidOperationException("Missing resource: Resources/TidalCompletion.txt");

      var list = new List<TidalCompletionItem>();
      using var reader = new StreamReader(info.Stream);
      string? line;
      while ((line = reader.ReadLine()) is not null)
      {
        if (string.IsNullOrWhiteSpace(line)) continue;
        var parts = line.Split('\t');
        if (parts.Length < 4) continue;
        var detail = parts[3];
        var doc = parts.Length > 4 ? string.Join('\t', parts.Skip(4)) : string.Empty;
        list.Add(new TidalCompletionItem(parts[0], parts[1], parts[2], detail, doc));
      }

      return list;
    }
  }

}
