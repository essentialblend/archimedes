// Services/HaskellIndentationStrategy.cs

using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Indentation;
using System;

namespace archimedes
{
  internal sealed class HaskellIndentationStrategy : IIndentationStrategy
  {
    private readonly TextEditorOptions _options;

    public HaskellIndentationStrategy(TextEditorOptions options)
    {
      _options = options;
    }

    public void IndentLine(TextDocument document, DocumentLine line)
    {
      if (line.LineNumber == 1) return;

      var prevLine = GetPreviousContentLine(document, line.LineNumber - 1);
      if (prevLine is null) return;

      var prevIndentSeg = TextUtilities.GetLeadingWhitespace(document, prevLine);
      var prevIndent = document.GetText(prevIndentSeg);
      var prevText = StripLineComment(document.GetText(prevLine)).TrimEnd();

      var newIndent = prevIndent;

      if (ShouldIndentAfter(prevText))
        newIndent += _options.IndentationString;

      var currentText = StripLineComment(document.GetText(line)).TrimStart();

      if (StartsWithOutdentToken(currentText))
        newIndent = RemoveIndentLevel(newIndent);

      if (currentText.StartsWith("|", StringComparison.Ordinal))
      {
        if (!prevText.TrimStart().StartsWith("|", StringComparison.Ordinal))
          newIndent = prevIndent + _options.IndentationString;
        else
          newIndent = prevIndent;
      }

      var existingIndent = TextUtilities.GetLeadingWhitespace(document, line);
      document.Replace(existingIndent.Offset, existingIndent.Length, newIndent);
    }

    public void IndentLines(TextDocument document, int beginLine, int endLine)
    {
      for (int i = beginLine; i <= endLine; i++)
        IndentLine(document, document.GetLineByNumber(i));
    }

    private static DocumentLine? GetPreviousContentLine(TextDocument document, int lineNumber)
    {
      for (int i = lineNumber; i >= 1; i--)
      {
        var line = document.GetLineByNumber(i);
        var text = document.GetText(line);
        if (!string.IsNullOrWhiteSpace(text))
          return line;
      }

      return null;
    }

    private static string StripLineComment(string text)
    {
      var idx = text.IndexOf("--", StringComparison.Ordinal);
      return idx >= 0 ? text[..idx] : text;
    }

    private static bool ShouldIndentAfter(string prevText)
    {
      if (string.IsNullOrWhiteSpace(prevText)) return false;

      var trimmed = prevText.TrimEnd();

      if (trimmed.EndsWith("{", StringComparison.Ordinal) ||
          trimmed.EndsWith("(", StringComparison.Ordinal) ||
          trimmed.EndsWith("[", StringComparison.Ordinal))
        return true;

      if (trimmed.EndsWith("=", StringComparison.Ordinal) ||
          trimmed.EndsWith("->", StringComparison.Ordinal) ||
          trimmed.EndsWith("<-", StringComparison.Ordinal))
        return true;

      return EndsWithKeyword(trimmed, "do") ||
             EndsWithKeyword(trimmed, "then") ||
             EndsWithKeyword(trimmed, "else") ||
             EndsWithKeyword(trimmed, "let") ||
             EndsWithKeyword(trimmed, "where") ||
             EndsWithKeyword(trimmed, "of");
    }

    private static bool EndsWithKeyword(string text, string keyword)
    {
      if (!text.EndsWith(keyword, StringComparison.Ordinal)) return false;

      var idx = text.Length - keyword.Length - 1;
      return idx < 0 || char.IsWhiteSpace(text[idx]) || text[idx] == '(';
    }

    private static bool StartsWithOutdentToken(string text)
    {
      if (string.IsNullOrWhiteSpace(text)) return false;

      if (text.StartsWith("}", StringComparison.Ordinal) ||
          text.StartsWith(")", StringComparison.Ordinal) ||
          text.StartsWith("]", StringComparison.Ordinal))
        return true;

      return StartsWithKeyword(text, "in") ||
             StartsWithKeyword(text, "else") ||
             StartsWithKeyword(text, "where") ||
             StartsWithKeyword(text, "of");
    }

    private static bool StartsWithKeyword(string text, string keyword)
    {
      if (!text.StartsWith(keyword, StringComparison.Ordinal)) return false;
      return text.Length == keyword.Length || char.IsWhiteSpace(text[keyword.Length]);
    }

    private string RemoveIndentLevel(string indent)
    {
      var indentUnit = _options.IndentationString;
      if (!string.IsNullOrEmpty(indentUnit) &&
          indent.EndsWith(indentUnit, StringComparison.Ordinal))
      {
        return indent[..^indentUnit.Length];
      }

      int remove = 0;
      for (int i = indent.Length - 1; i >= 0 && remove < _options.IndentationSize; i--)
      {
        if (indent[i] != ' ') break;
        remove++;
      }

      return remove > 0 ? indent[..^remove] : indent;
    }
  }
}
