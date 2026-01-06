// Views/TidalCompletionData.cs

using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace archimedes.Views
{
  internal sealed class TidalCompletionData : ICompletionData
  {
    private const string NoDescription = "No description available.";

    public TidalCompletionItem Item { get; }

    public string Name => Item.Name;
    public string Kind => Item.Kind;
    public string Module => Item.Module;
    public string Detail => Item.Detail;
    public string Doc => Item.Doc;
    public string SignatureLabel => string.Equals(Item.Kind, "Param", StringComparison.OrdinalIgnoreCase) ? "Type" : "Signature";
    public string SignatureText => Item.Detail;
    public bool HasSignature => !string.IsNullOrWhiteSpace(SignatureText);
    public IReadOnlyList<string> DocParagraphs { get; }
    public IReadOnlyList<CompletionExample> ExampleItems { get; }
    public string SummaryDescription { get; }
    public string SummaryExampleCode { get; }
    public string SummaryExampleNote { get; }
    public bool HasSummaryExample => !string.IsNullOrWhiteSpace(SummaryExampleCode);
    public string DocDescription { get; }
    public string Examples { get; }
    public bool HasExamples => ExampleItems.Count > 0;
    public bool HasDescription => !string.IsNullOrWhiteSpace(SummaryDescription) && !string.Equals(SummaryDescription, NoDescription, StringComparison.Ordinal);

    public ImageSource? Image => null;

    public string Text => Item.Name;

    public object Content => Item.Name;

    public object? Description => BuildDescriptionView();

    public double Priority => 0;

    public TidalCompletionData(TidalCompletionItem item)
    {
      Item = item ?? throw new ArgumentNullException(nameof(item));
      var parsed = ParseDoc(item.Doc);
      DocParagraphs = parsed.DescriptionParagraphs;
      ExampleItems = parsed.Examples;
      SummaryDescription = BuildSummaryDescription(DocParagraphs, item);
      (SummaryExampleCode, SummaryExampleNote) = BuildSummaryExample(ExampleItems, item);
      DocDescription = string.Join("\n\n", DocParagraphs);
      Examples = string.Join("\n", ExampleItems.Select(ex => ex.DisplayCode));
    }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
      textArea.Document.Replace(completionSegment, Item.Name);
    }

    private sealed record CompletionDoc(IReadOnlyList<string> DescriptionParagraphs, IReadOnlyList<CompletionExample> Examples);

    internal sealed record CompletionExample(string Code, string Note)
    {
      public string DisplayCode => $"> {Code}";
      public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    }

    private static readonly Regex InlineExampleRegex = new(@"\\bd\\d+\\s*\\$\\s*[^\\n\\.]+", RegexOptions.Compiled);

    private static CompletionDoc ParseDoc(string doc)
    {
      if (string.IsNullOrWhiteSpace(doc))
        return new CompletionDoc(new[] { NoDescription }, Array.Empty<CompletionExample>());

      var normalized = doc.Replace("\r\n", "\n").Replace("\r", "\n");
      var parts = normalized.Split(new[] { " > " }, StringSplitOptions.None);
      var description = parts[0].Trim();

      if (string.IsNullOrWhiteSpace(description))
        description = NoDescription;

      var descriptionParagraphs = SplitDescription(description);

      if (parts.Length <= 1)
        return new CompletionDoc(descriptionParagraphs, Array.Empty<CompletionExample>());

      var examples = parts.Skip(1)
        .Select(p => p.Trim())
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(ParseExample)
        .Where(ex => !string.IsNullOrWhiteSpace(ex.Code))
        .ToList();

      if (examples.Count == 0)
        examples = ExtractInlineExamples(normalized);

      return new CompletionDoc(descriptionParagraphs, examples);
    }

    private static IReadOnlyList<string> SplitDescription(string description)
    {
      if (string.IsNullOrWhiteSpace(description))
        return new[] { NoDescription };

      if (string.Equals(description, NoDescription, StringComparison.Ordinal))
        return new[] { NoDescription };

      var paragraphs = new System.Collections.Generic.List<string>();
      var lines = description.Split('\n', StringSplitOptions.RemoveEmptyEntries);
      foreach (var rawLine in lines)
      {
        var line = rawLine.Trim();
        if (string.IsNullOrWhiteSpace(line)) continue;

        var sentences = line.Split(new[] { ". " }, StringSplitOptions.RemoveEmptyEntries)
          .Select(s => s.Trim())
          .Where(s => s.Length > 0)
          .Select(s => s.EndsWith(".", StringComparison.Ordinal) ? s : $"{s}.")
          .ToList();

        if (sentences.Count == 0) continue;

        const int maxLength = 140;
        const int maxSentences = 1;
        var current = string.Empty;
        var sentenceCount = 0;

        foreach (var sentence in sentences)
        {
          var proposed = string.IsNullOrEmpty(current) ? sentence : $"{current} {sentence}";
          if (!string.IsNullOrEmpty(current) && (proposed.Length > maxLength || sentenceCount >= maxSentences))
          {
            paragraphs.Add(current);
            current = sentence;
            sentenceCount = 1;
          }
          else
          {
            current = proposed;
            sentenceCount++;
          }
        }

        if (!string.IsNullOrWhiteSpace(current))
          paragraphs.Add(current);
      }

      if (paragraphs.Count == 0)
        paragraphs.Add(NoDescription);

      return paragraphs;
    }

    private static CompletionExample ParseExample(string example)
    {
      var trimmed = example.Trim();
      if (trimmed.StartsWith("> ", StringComparison.Ordinal))
        trimmed = trimmed[2..].TrimStart();

      if (string.IsNullOrWhiteSpace(trimmed))
        return new CompletionExample(string.Empty, string.Empty);

      var noteIndex = FindExampleNoteIndex(trimmed);
      if (noteIndex < 0)
        return new CompletionExample(trimmed, string.Empty);

      var code = trimmed[..noteIndex].TrimEnd();
      var note = trimmed[noteIndex..].TrimStart();
      return new CompletionExample(code, note);
    }

    private static int FindExampleNoteIndex(string text)
    {
      string[] markers =
      {
        " This ",
        " You ",
        " In this ",
        " In the ",
        " For ",
        " It ",
        " Use ",
        " Used ",
        " These "
      };

      var bestIndex = -1;
      foreach (var marker in markers)
      {
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index <= 0) continue;
        if (bestIndex < 0 || index < bestIndex)
          bestIndex = index;
      }

      return bestIndex;
    }

    private static string BuildSummaryDescription(IReadOnlyList<string> paragraphs, TidalCompletionItem item)
    {
      if (paragraphs.Count == 0)
        return BuildFallbackDescription(item);

      var first = paragraphs[0];
      if (string.Equals(first, NoDescription, StringComparison.Ordinal))
        return BuildFallbackDescription(item);

      return TrimToLength(first, 180);
    }

    private static (string code, string note) BuildSummaryExample(IReadOnlyList<CompletionExample> examples, TidalCompletionItem item)
    {
      if (examples.Count == 0)
      {
        if (string.Equals(item.Kind, "Param", StringComparison.OrdinalIgnoreCase))
          return ($"d1 $ sound \"bd\" # {item.Name} 1", string.Empty);

        return (string.Empty, string.Empty);
      }

      var first = examples[0];
      var code = TrimToLength(first.Code.Replace('\n', ' ').Trim(), 120);
      var note = TrimToLength(first.Note.Replace('\n', ' ').Trim(), 120);
      return (code, note);
    }

    private static string TrimToLength(string text, int maxLength)
    {
      if (string.IsNullOrWhiteSpace(text)) return string.Empty;
      if (text.Length <= maxLength) return text;

      var trimmed = text[..maxLength];
      var lastSpace = trimmed.LastIndexOf(' ');
      if (lastSpace > 0)
        trimmed = trimmed[..lastSpace];

      return $"{trimmed}...";
    }

    private static string BuildFallbackDescription(TidalCompletionItem item)
    {
      var module = string.IsNullOrWhiteSpace(item.Module) ? "Tidal" : item.Module;
      if (string.Equals(item.Kind, "Param", StringComparison.OrdinalIgnoreCase))
        return $"Control parameter from {module}.";

      if (string.Equals(item.Kind, "Boot", StringComparison.OrdinalIgnoreCase))
        return $"Boot helper from {module}.";

      if (string.Equals(item.Kind, "Function", StringComparison.OrdinalIgnoreCase))
        return $"Pattern function from {module}.";

      return $"Tidal symbol from {module}.";
    }

    private static List<CompletionExample> ExtractInlineExamples(string text)
    {
      var results = new List<CompletionExample>();
      foreach (Match match in InlineExampleRegex.Matches(text))
      {
        var code = match.Value.Trim();
        if (string.IsNullOrWhiteSpace(code)) continue;
        results.Add(new CompletionExample(code, string.Empty));
        if (results.Count >= 2) break;
      }

      return results;
    }

    private object BuildDescriptionView()
    {
      var template = Application.Current?.TryFindResource("CompletionDetailTemplate") as DataTemplate;
      if (template is null)
      {
        var foreground = Application.Current?.TryFindResource("Brush.Text.Main") as Brush
          ?? SystemColors.ControlTextBrush;
        return new TextBlock
        {
          Text = DocDescription,
          TextWrapping = TextWrapping.Wrap,
          Foreground = foreground,
          MaxWidth = 360
        };
      }

      return new ContentControl
      {
        Content = this,
        ContentTemplate = template
      };
    }
  }
}
