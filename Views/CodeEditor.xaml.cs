// Views/CodeEditor.xaml.cs

using archimedes;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;

namespace archimedes.Views
{
  internal interface ICodeEditorThemeApplier
  {
    void Apply(TextEditor editor, IHighlightingDefinition? highlighting);
  }

  internal sealed class CodeEditorThemeApplier : ICodeEditorThemeApplier
  {
    public void Apply(TextEditor editor, IHighlightingDefinition? highlighting)
    {
      // Caret
      if (Application.Current?.TryFindResource("Brush.Editor.Caret") is SolidColorBrush caret)
        editor.TextArea.Caret.CaretBrush = caret;

      if (highlighting is null) return;

      ApplyNamed(highlighting, "Comment", "Brush.Syntax.Comment");
      ApplyNamed(highlighting, "String", "Brush.Syntax.String");
      ApplyNamed(highlighting, "Escape", "Brush.Syntax.Escape");
      ApplyNamed(highlighting, "Number", "Brush.Syntax.Number");
      ApplyNamed(highlighting, "TidalStream", "Brush.Syntax.Stream");
      ApplyNamed(highlighting, "Operator", "Brush.Syntax.Operator");
      ApplyNamed(highlighting, "Comma", "Brush.Syntax.Comma");

      // Force refresh
      editor.SyntaxHighlighting = null;
      editor.SyntaxHighlighting = highlighting;
    }

    private static void ApplyNamed(IHighlightingDefinition def, string name, string brushKey)
    {
      if (Application.Current?.TryFindResource(brushKey) is not SolidColorBrush b) return;

      var c = def.GetNamedColor(name);
      if (c is null) return;

      c.Foreground = new SimpleHighlightingBrush(b.Color);
    }
  }

  internal interface IEditorZoomController
  {
    bool TryKey(KeyEventArgs e, double cur, out double next);
    bool TryWheel(MouseWheelEventArgs e, double cur, out double next);
    double Clamp(double pct);
  }

  internal sealed class EditorZoomController : IEditorZoomController
  {
    private readonly double _step, _min, _max;
    public EditorZoomController(double step = 10, double min = 50, double max = 500)
    { _step = step; _min = min; _max = max; }

    public double Clamp(double pct) => Math.Max(_min, Math.Min(_max, pct));

    public bool TryKey(KeyEventArgs e, double cur, out double next)
    {
      next = cur;
      if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return false;

      if (e.Key is Key.OemPlus or Key.Add) { next = cur + _step; return true; }
      if (e.Key is Key.OemMinus or Key.Subtract) { next = cur - _step; return true; }
      if (e.Key is Key.D0 or Key.NumPad0) { next = 100; return true; }
      return false;
    }

    public bool TryWheel(MouseWheelEventArgs e, double cur, out double next)
    {
      next = cur;
      if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return false;
      next = cur + (e.Delta > 0 ? _step : -_step);
      return true;
    }
  }

  public partial class CodeEditor : UserControl
  {
    private readonly CodeEditorThemeApplier _themeApplier = new();
    private readonly EditorSettingsStore _settings = EditorSettingsStore.Load();
    private readonly DiagnosticsUnderlineRenderer _diagnosticRenderer = new();
    private const double TextLeftPadding = 8;
    private const double WrapIndicatorPadding = 14;
    private Thickness _baseEditorPadding = new Thickness(12);

    private IHighlightingDefinition? _tidal;
    private bool _editorWired;
    private double _baseFontSize = 13;

    private readonly EditorZoomController _zoomCtrl = new();
    private double _zoomPct = 100;
    private bool _syncingZoomBox;
    private bool _syncingWrapToggle;
    private bool _pendingChord;
    private bool _syncingGoToLine;

    public CodeEditor()
    {
      InitializeComponent();
      Loaded += OnControlLoaded;
      Unloaded += OnControlUnloaded;
    }

    private static IHighlightingDefinition LoadTidalHaskellHighlighting()
    {
      var info = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/Syntax/TidalHaskell.xshd", UriKind.Absolute))
                 ?? Application.GetResourceStream(new Uri("pack://application:,,,/archimedes;component/Resources/Syntax/TidalHaskell.xshd", UriKind.Absolute))
                 ?? throw new InvalidOperationException("Missing resource: Resources/Syntax/TidalHaskell.xshd");

      using var stream = info.Stream;
      using var reader = XmlReader.Create(stream);
      return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private void OnEditorLoaded(object sender, RoutedEventArgs e)
    {
      WireEditorOnce();

      try
      {
        _tidal = LoadTidalHaskellHighlighting();
        Editor.SyntaxHighlighting = _tidal;
      }
      catch (Exception ex)
      {
        Editor.SyntaxHighlighting = null; // keep editor usable
        _tidal = null;
        MessageBox.Show(ex.ToString(), "TidalHaskell.xshd load failed");
      }

      ApplyTheme();
      UpdateCaretText();
      UpdateSelectionText();
      UpdateGoToLineBox();
      ApplyWordWrap(_settings.WordWrap, persist: false);
      SetZoom(GetZoomPctFromComboBox());
      UpdateDiagnostics();
    }

    private void WireEditorOnce()
    {
      if (_editorWired) return;

      _baseFontSize = Editor.FontSize;
      _baseEditorPadding = Editor.Padding;
      UpdateTextViewMargin(_settings.WordWrap);
      Editor.Options.IndentationSize = 2;
      Editor.Options.ConvertTabsToSpaces = true;
      Editor.TextArea.IndentationStrategy = new HaskellIndentationStrategy(Editor.Options);
      WrapIndicator.Editor = Editor;
      WrapIndicator.Enabled = _settings.WordWrap;
      UpdateWrapIndicatorBrush();
      _diagnosticRenderer.Editor = Editor;
      Editor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticRenderer);
      UpdateDiagnosticsBrush();

      ZoomBox.SelectionChanged += OnZoomSelectionChanged;
      WrapToggle.Checked += OnWrapToggleChanged;
      WrapToggle.Unchecked += OnWrapToggleChanged;
      Editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
      Editor.TextArea.SelectionChanged += OnSelectionChanged;
      Editor.TextChanged += OnEditorTextChanged;
      GoToLineBox.PreviewKeyDown += OnGoToLinePreviewKeyDown;
      Editor.PreviewKeyDown += OnEditorPreviewKeyDown;
      Editor.PreviewMouseWheel += OnEditorPreviewMouseWheel;

      _editorWired = true;
    }

    private void OnEditorPreviewKeyDown(object s, KeyEventArgs e)
    {
      if (HandleEditorChord(e)) { e.Handled = true; return; }
      if (_zoomCtrl.TryKey(e, _zoomPct, out var next)) { SetZoom(next); e.Handled = true; }
    }
    private void OnEditorPreviewMouseWheel(object s, MouseWheelEventArgs e)
    {
      if (_zoomCtrl.TryWheel(e, _zoomPct, out var next)) { SetZoom(next); e.Handled = true; }
    }
    private void SetZoom(double pct)
    {
      _zoomPct = _zoomCtrl.Clamp(pct);
      Editor.FontSize = _baseFontSize * (_zoomPct / 100.0);
      UpdateZoomBox(_zoomPct);
    }

    private void OnControlLoaded(object? sender, RoutedEventArgs e)
    {
      archimedes.ResourceDictionaryThemeToggler.ThemeChanged += OnThemeChanged;
    }

    private void OnControlUnloaded(object? sender, RoutedEventArgs e)
    {
      archimedes.ResourceDictionaryThemeToggler.ThemeChanged -= OnThemeChanged;

      if (_editorWired)
      {
        ZoomBox.SelectionChanged -= OnZoomSelectionChanged;
        WrapToggle.Checked -= OnWrapToggleChanged;
        WrapToggle.Unchecked -= OnWrapToggleChanged;
        Editor.TextArea.Caret.PositionChanged -= OnCaretPositionChanged;
        Editor.TextArea.SelectionChanged -= OnSelectionChanged;
        Editor.TextChanged -= OnEditorTextChanged;
        GoToLineBox.PreviewKeyDown -= OnGoToLinePreviewKeyDown;
        WrapIndicator.Editor = null;
        Editor.TextArea.TextView.BackgroundRenderers.Remove(_diagnosticRenderer);
        _diagnosticRenderer.Editor = null;
        _editorWired = false;
      }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    public void ApplyTheme()
    {
      _themeApplier.Apply(Editor, _tidal ?? Editor.SyntaxHighlighting);
      UpdateWrapIndicatorBrush();
      UpdateDiagnosticsBrush();
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
      UpdateCaretText();
      UpdateGoToLineBox();
    }
    private void OnSelectionChanged(object? sender, EventArgs e) => UpdateSelectionText();

    private void UpdateCaretText()
    {
      CaretText.Text = $"Ln: {Editor.TextArea.Caret.Line}, Col: {Editor.TextArea.Caret.Column}";
    }

    private void UpdateSelectionText()
    {
      SelText.Text = $"Sel: {Editor.SelectionLength}";
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
      UpdateDiagnostics();
      UpdateGoToLineBox();
    }

    private bool HandleEditorChord(KeyEventArgs e)
    {
      var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

      if (!_pendingChord && ctrl && e.Key == Key.G)
      {
        FocusGoToLineBox();
        return true;
      }

      if (_pendingChord)
      {
        _pendingChord = false;
        if (!ctrl) return false;

        if (e.Key == Key.C) { CommentSelection(); return true; }
        if (e.Key == Key.U) { UncommentSelection(); return true; }
        if (e.Key == Key.D) { FormatSelection(); return true; }

        return false;
      }

      if (ctrl && e.Key == Key.K)
      {
        _pendingChord = true;
        return true;
      }

      return false;
    }

    private void OnWrapToggleChanged(object? sender, RoutedEventArgs e)
    {
      if (_syncingWrapToggle) return;
      ApplyWordWrap(WrapToggle.IsChecked == true, persist: true);
    }

    private void ApplyWordWrap(bool enabled, bool persist)
    {
      Editor.WordWrap = enabled;
      UpdateTextViewMargin(enabled);
      WrapIndicator.Enabled = enabled;
      WrapIndicator.InvalidateVisual();

      _syncingWrapToggle = true;
      try
      {
        WrapToggle.IsChecked = enabled;
      }
      finally
      {
        _syncingWrapToggle = false;
      }

      if (persist)
      {
        _settings.WordWrap = enabled;
        _settings.Save();
      }
    }

    private void UpdateWrapIndicatorBrush()
    {
      var brush = Application.Current?.TryFindResource("Brush.Text.Dim") as Brush;
      WrapIndicator.UpdateBrush(brush);
    }

    private void UpdateTextViewMargin(bool wrapEnabled)
    {
      var rightPad = wrapEnabled ? WrapIndicatorPadding : 0;
      Editor.Padding = new Thickness(
        _baseEditorPadding.Left,
        _baseEditorPadding.Top,
        _baseEditorPadding.Right + rightPad,
        _baseEditorPadding.Bottom);

      Editor.TextArea.TextView.Margin = new Thickness(TextLeftPadding, 0, 0, 0);
    }

    private void UpdateDiagnostics()
    {
      var doc = Editor.Document;
      if (doc is null)
      {
        DiagText.Text = "No issues found";
        _diagnosticRenderer.SetIssues(Array.Empty<DiagnosticIssue>());
        return;
      }

      var issues = EvaluateDiagnostics(doc.Text);
      _diagnosticRenderer.SetIssues(issues);
      if (issues.Count == 0)
      {
        DiagText.Text = "No issues found";
        return;
      }

      DiagText.Text = issues.Count == 1 ? issues[0].Message : $"{issues.Count} issues found";
    }

    private List<DiagnosticIssue> EvaluateDiagnostics(string text)
    {
      // Fast, file-local checks only: brackets, strings/comments, and layout indentation.
      var issues = new List<DiagnosticIssue>();
      var stack = new Stack<(char open, int offset)>();

      bool inString = false;
      bool inLineComment = false;
      int blockCommentDepth = 0;
      int stringStart = -1;
      var blockCommentStarts = new Stack<int>();
      bool badCloser = false;
      char badCloserChar = '\0';
      int badCloserOffset = -1;

      for (int i = 0; i < text.Length; i++)
      {
        var c = text[i];
        var next = i + 1 < text.Length ? text[i + 1] : '\0';

        if (inLineComment)
        {
          if (c == '\n') inLineComment = false;
          continue;
        }

        if (blockCommentDepth > 0)
        {
          if (c == '{' && next == '-')
          {
            blockCommentDepth++;
            blockCommentStarts.Push(i);
            i++;
          }
          else if (c == '-' && next == '}')
          {
            blockCommentDepth--;
            if (blockCommentStarts.Count > 0) blockCommentStarts.Pop();
            i++;
          }
          continue;
        }

        if (inString)
        {
          if (c == '\\' && i + 1 < text.Length)
          {
            i++;
            continue;
          }
          if (c == '"') inString = false;
          continue;
        }

        if (c == '-' && next == '-')
        {
          inLineComment = true;
          i++;
          continue;
        }

        if (c == '{' && next == '-')
        {
          blockCommentDepth = 1;
          blockCommentStarts.Push(i);
          i++;
          continue;
        }

        if (c == '"')
        {
          inString = true;
          stringStart = i;
          continue;
        }

        if (c is '(' or '[' or '{')
        {
          stack.Push((c, i));
          continue;
        }

        if (c is ')' or ']' or '}')
        {
          if (stack.Count == 0 || !BracketsMatch(stack.Peek().open, c))
          {
            badCloser = true;
            badCloserChar = c;
            badCloserOffset = i;
            break;
          }
          stack.Pop();
        }
      }

      if (badCloser)
        issues.Add(new DiagnosticIssue(badCloserOffset, 1, $"Unmatched closing '{badCloserChar}'"));
      if (inString)
        issues.Add(new DiagnosticIssue(Math.Max(0, stringStart), 1, "Unterminated string literal"));
      if (blockCommentDepth > 0)
      {
        var start = blockCommentStarts.Count > 0 ? blockCommentStarts.Peek() : Math.Max(0, text.Length - 1);
        issues.Add(new DiagnosticIssue(start, 2, "Unterminated block comment"));
      }
      while (stack.Count > 0)
      {
        var (open, offset) = stack.Pop();
        issues.Add(new DiagnosticIssue(offset, 1, $"Missing closing '{ExpectedClosing(open)}'"));
      }

      var indentIssue = FindIndentIssue(text);
      if (indentIssue is not null)
        issues.Add(indentIssue);

      return issues;
    }

    private static bool BracketsMatch(char open, char close)
    {
      return (open == '(' && close == ')') ||
             (open == '[' && close == ']') ||
             (open == '{' && close == '}');
    }

    private static char ExpectedClosing(char open)
    {
      return open switch
      {
        '(' => ')',
        '[' => ']',
        '{' => '}',
        _ => '?'
      };
    }

    private static DiagnosticIssue? FindIndentIssue(string text)
    {
      // Heuristic: after a layout opener, the next non-blank line should be indented.
      int lineStart = 0;
      while (lineStart < text.Length)
      {
        var lineLen = GetLineLength(text, lineStart, out var newlineLen);
        if (lineLen == 0 && newlineLen == 0) break;
        var line = text.Substring(lineStart, lineLen);
        var trimmed = StripLineComment(line).TrimEnd();
        if (!string.IsNullOrWhiteSpace(trimmed))
        {
          if (ShouldIndentAfter(trimmed))
          {
            var nextLineStart = lineStart + lineLen + newlineLen;
            while (nextLineStart < text.Length)
            {
              var nextLen = GetLineLength(text, nextLineStart, out var nextNewlineLen);
              var nextLine = text.Substring(nextLineStart, nextLen);
              if (!string.IsNullOrWhiteSpace(nextLine))
              {
                var curIndent = LeadingWhitespaceCount(line);
                var nextIndent = LeadingWhitespaceCount(nextLine);
                if (nextIndent <= curIndent)
                {
                  var keywordIndex = FindLayoutKeywordIndex(line);
                  var offset = lineStart + Math.Max(0, keywordIndex);
                  return new DiagnosticIssue(offset, 1, "Expected indentation after layout keyword");
                }
                break;
              }
              nextLineStart += nextLen + nextNewlineLen;
            }
          }
        }

        lineStart += lineLen + newlineLen;
      }

      return null;
    }

    private static int LeadingWhitespaceCount(string line)
    {
      int count = 0;
      foreach (var c in line)
      {
        if (c == ' ' || c == '\t') count++;
        else break;
      }
      return count;
    }

    private static string StripLineComment(string text)
    {
      var idx = text.IndexOf("--", StringComparison.Ordinal);
      return idx >= 0 ? text[..idx] : text;
    }

    private static int GetLineLength(string text, int start, out int newlineLen)
    {
      newlineLen = 0;
      if (start >= text.Length) return 0;

      int i = start;
      while (i < text.Length && text[i] != '\r' && text[i] != '\n') i++;

      if (i < text.Length && text[i] == '\r')
      {
        newlineLen = (i + 1 < text.Length && text[i + 1] == '\n') ? 2 : 1;
      }
      else if (i < text.Length && text[i] == '\n')
      {
        newlineLen = 1;
      }

      return i - start;
    }

    private static int FindLayoutKeywordIndex(string line)
    {
      var trimmed = StripLineComment(line).TrimEnd();
      if (string.IsNullOrWhiteSpace(trimmed)) return line.Length;

      var index = trimmed.Length - 1;
      while (index >= 0 && char.IsWhiteSpace(trimmed[index])) index--;
      if (index < 0) return line.Length;

      var tokenStart = index;
      while (tokenStart >= 0 && !char.IsWhiteSpace(trimmed[tokenStart])) tokenStart--;

      var token = trimmed[(tokenStart + 1)..(index + 1)];
      var pos = line.LastIndexOf(token, StringComparison.Ordinal);
      return pos >= 0 ? pos : line.Length;
    }

    private static bool ShouldIndentAfter(string prevText)
    {
      var trimmed = prevText.TrimEnd();
      if (string.IsNullOrWhiteSpace(trimmed)) return false;

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

    private void UpdateDiagnosticsBrush()
    {
      var brush = Application.Current?.TryFindResource("Brush.Acc.Red") as Brush;
      _diagnosticRenderer.UpdateBrush(brush);
    }

    private TextBox? GetGoToLineTextBox()
    {
      GoToLineBox.ApplyTemplate();
      return GoToLineBox.Template.FindName("PART_EditableTextBox", GoToLineBox) as TextBox
             ?? GoToLineBox.Template.FindName("EditableTextBox", GoToLineBox) as TextBox;
    }

    private void FocusGoToLineBox()
    {
      GoToLineBox.IsDropDownOpen = false;
      GoToLineBox.Focus();
      GoToLineBox.Dispatcher.BeginInvoke(new Action(() =>
      {
        var box = GetGoToLineTextBox();
        if (box is null) return;
        box.Focus();
        box.SelectAll();
      }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void UpdateGoToLineBox()
    {
      if (_syncingGoToLine) return;
      if (GoToLineBox.IsKeyboardFocusWithin) return;

      _syncingGoToLine = true;
      try
      {
        GoToLineBox.Text = Editor.TextArea.Caret.Line.ToString(CultureInfo.InvariantCulture);
      }
      finally
      {
        _syncingGoToLine = false;
      }
    }

    private void OnGoToLinePreviewKeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Escape)
      {
        Editor.TextArea.Focus();
        e.Handled = true;
        return;
      }
      if (e.Key is not (Key.Enter or Key.Return)) return;
      var text = GetGoToLineTextBox()?.Text ?? GoToLineBox.Text;
      if (TryGoToLine(text))
      {
        var box = GetGoToLineTextBox();
        if (box is not null) box.Clear();
        GoToLineBox.Text = string.Empty;
        e.Handled = true;
      }
    }

    private bool TryGoToLine(string? text)
    {
      if (Editor.Document is null) return false;
      if (string.IsNullOrWhiteSpace(text)) return false;

      if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var line))
        return false;

      var maxLine = Editor.Document.LineCount;
      line = Math.Max(1, Math.Min(maxLine, line));

      Editor.TextArea.Caret.Line = line;
      Editor.TextArea.Caret.BringCaretToView();
      Editor.ScrollToLine(line);
      UpdateGoToLineBox();
      return true;
    }

    private void CommentSelection()
    {
      var doc = Editor.Document;
      if (doc is null) return;

      var lineRange = GetSelectedLineRange();
      if (lineRange is null) return;

      using (doc.RunUpdate())
      {
        for (var line = lineRange.Value.start; line is not null && line.LineNumber <= lineRange.Value.end.LineNumber; line = line.NextLine)
        {
          var indentSeg = TextUtilities.GetLeadingWhitespace(doc, line);
          var insertAt = indentSeg.Offset + indentSeg.Length;
          doc.Insert(insertAt, "-- ");
        }
      }
    }

    private void UncommentSelection()
    {
      var doc = Editor.Document;
      if (doc is null) return;

      var lineRange = GetSelectedLineRange();
      if (lineRange is null) return;

      using (doc.RunUpdate())
      {
        for (var line = lineRange.Value.start; line is not null && line.LineNumber <= lineRange.Value.end.LineNumber; line = line.NextLine)
        {
          var indentSeg = TextUtilities.GetLeadingWhitespace(doc, line);
          var offset = indentSeg.Offset + indentSeg.Length;
          if (offset + 1 >= line.EndOffset) continue;

          var text = doc.GetText(line);
          var local = offset - line.Offset;
          if (local + 1 >= text.Length) continue;

          if (text.AsSpan(local).StartsWith("--", StringComparison.Ordinal))
          {
            var removeLen = 2;
            if (local + 2 < text.Length && text[local + 2] == ' ')
              removeLen = 3;

            doc.Remove(offset, removeLen);
          }
        }
      }
    }

    private void FormatSelection()
    {
      var doc = Editor.Document;
      if (doc is null || Editor.TextArea.IndentationStrategy is null) return;

      var range = GetSelectedLineRange();
      if (range is null)
      {
        Editor.TextArea.IndentationStrategy.IndentLines(doc, 1, doc.LineCount);
        return;
      }

      Editor.TextArea.IndentationStrategy.IndentLines(doc, range.Value.start.LineNumber, range.Value.end.LineNumber);
    }

    private (DocumentLine start, DocumentLine end)? GetSelectedLineRange()
    {
      var doc = Editor.Document;
      if (doc is null) return null;

      if (Editor.SelectionLength == 0)
      {
        var line = doc.GetLineByOffset(Editor.CaretOffset);
        return (line, line);
      }

      var startLine = doc.GetLineByOffset(Editor.SelectionStart);
      var endOffset = Editor.SelectionStart + Editor.SelectionLength;
      var endLine = doc.GetLineByOffset(Math.Max(Editor.SelectionStart, endOffset - 1));

      if (endOffset > Editor.SelectionStart && endOffset == endLine.Offset && endLine.PreviousLine is not null)
        endLine = endLine.PreviousLine;

      return (startLine, endLine);
    }

    private void OnZoomSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (_syncingZoomBox) return;

      if (ZoomBox.SelectedItem is ComboBoxItem it &&
          TryParseZoomPercent(it.Content?.ToString(), out var selectedPct))
      {
        SetZoom(selectedPct);
        return;
      }

      if (TryParseZoomPercent(ZoomBox.Text, out var typedPct))
      {
        SetZoom(typedPct);
      }
    }

    private double GetZoomPctFromComboBox()
    {
      if (ZoomBox.SelectedItem is ComboBoxItem it &&
          TryParseZoomPercent(it.Content?.ToString(), out var pct))
      {
        return pct;
      }

      if (TryParseZoomPercent(ZoomBox.Text, out var typedPct)) return typedPct;

      return _zoomPct;
    }

    private void UpdateZoomBox(double pct)
    {
      _syncingZoomBox = true;
      try
      {
        var match = FindZoomItem(pct);
        if (match is not null)
        {
          ZoomBox.SelectedItem = match;
        }
        else
        {
          ZoomBox.SelectedIndex = -1;
        }

        ZoomBox.Text = $"{pct:0}%";
      }
      finally
      {
        _syncingZoomBox = false;
      }
    }

    private ComboBoxItem? FindZoomItem(double pct)
    {
      foreach (var item in ZoomBox.Items)
      {
        if (item is ComboBoxItem combo &&
            TryParseZoomPercent(combo.Content?.ToString(), out var itemPct) &&
            Math.Abs(itemPct - pct) < 0.1)
        {
          return combo;
        }
      }

      return null;
    }

    private static bool TryParseZoomPercent(string? text, out double pct)
    {
      pct = 0;
      if (string.IsNullOrWhiteSpace(text)) return false;

      text = text.Trim();
      if (text.EndsWith("%", StringComparison.Ordinal))
        text = text[..^1];

      return double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out pct);
    }
  }
}
