// Views/CodeEditor.xaml.cs

using archimedes;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Rendering;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly EditorBufferStore _bufferStore = new();
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
    private MoveLineInputHandler? _moveLineHandler;
    private ArchimedesCompletionWindow? _completionWindow;
    private IReadOnlyList<TidalCompletionData>? _completionData;
    private EvalSnapshot? _evalSnapshot;
    private readonly List<DocumentChange> _evalChanges = new();
    private readonly Stopwatch _patternEditGate = Stopwatch.StartNew();
    private const int PatternEditHoldMs = 140;
    private readonly EvalFlashRenderer _evalFlashRenderer = new();
    private readonly EvalFlashRenderer _patternFlashRenderer = new();
    private readonly DispatcherTimer _evalFlashTimer = new();
    private readonly DispatcherTimer _diagnosticTimer = new();
    private readonly DispatcherTimer _bufferSaveTimer = new();
    private const int EvalFlashDurationMs = 450;
    private const int PatternFlashDurationMs = 220;
    private const int EvalFlashFrameMs = 33;
    private const int DiagnosticDebounceMs = 350;
    private const int BufferSaveDebounceMs = 500;
    private bool _loadingBuffer;

    public event EventHandler<string>? EvalRequested;

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
      if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(this)) return;
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
      ApplyWordWrap(_settings.WordWrap, persist: false);
      SetZoom(GetZoomPctFromComboBox());
      LoadEditorBuffer();
      UpdateCaretText();
      UpdateSelectionText();
      UpdateGoToLineBox();
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
      Editor.Options.EnableRectangularSelection = true;
      Editor.TextArea.IndentationStrategy = new HaskellIndentationStrategy(Editor.Options);
      WrapIndicator.Editor = Editor;
      WrapIndicator.Enabled = _settings.WordWrap;
      UpdateWrapIndicatorBrush();
      _diagnosticRenderer.Editor = Editor;
      Editor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticRenderer);
      _patternFlashRenderer.Editor = Editor;
      _patternFlashRenderer.Duration = TimeSpan.FromMilliseconds(PatternFlashDurationMs);
      _patternFlashRenderer.BaseAlpha = 200;
      _patternFlashRenderer.OutlineOnly = true;
      _patternFlashRenderer.OutlineThickness = 0.6;
      Editor.TextArea.TextView.BackgroundRenderers.Add(_patternFlashRenderer);
      _evalFlashRenderer.Editor = Editor;
      _evalFlashRenderer.Duration = TimeSpan.FromMilliseconds(EvalFlashDurationMs);
      Editor.TextArea.TextView.BackgroundRenderers.Add(_evalFlashRenderer);
      UpdateDiagnosticsBrush();
      UpdatePatternFlashBrush();
      UpdateEvalFlashBrush();
      _moveLineHandler = new MoveLineInputHandler(Editor.TextArea, MoveLines);
      Editor.TextArea.PushStackedInputHandler(_moveLineHandler);

      ZoomBox.SelectionChanged += OnZoomSelectionChanged;
      WrapToggle.Checked += OnWrapToggleChanged;
      WrapToggle.Unchecked += OnWrapToggleChanged;
      Editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
      Editor.TextArea.SelectionChanged += OnSelectionChanged;
      Editor.TextChanged += OnEditorTextChanged;
      if (Editor.Document is not null)
        Editor.Document.Changed += OnDocumentChanged;
      _diagnosticTimer.Interval = TimeSpan.FromMilliseconds(DiagnosticDebounceMs);
      _diagnosticTimer.Tick += OnDiagnosticTimerTick;
      _evalFlashTimer.Interval = TimeSpan.FromMilliseconds(EvalFlashFrameMs);
      _evalFlashTimer.Tick += OnEvalFlashTimerTick;
      _bufferSaveTimer.Interval = TimeSpan.FromMilliseconds(BufferSaveDebounceMs);
      _bufferSaveTimer.Tick += OnBufferSaveTimerTick;
      GoToLineBox.PreviewKeyDown += OnGoToLinePreviewKeyDown;
      Editor.TextArea.PreviewKeyDown += OnEditorPreviewKeyDown;
      Editor.TextArea.TextEntering += OnTextAreaTextEntering;
      Editor.TextArea.TextEntered += OnTextAreaTextEntered;
      Editor.PreviewMouseWheel += OnEditorPreviewMouseWheel;

      _editorWired = true;
    }

    private void OnEditorPreviewKeyDown(object s, KeyEventArgs e)
    {
      if (TryHandleCompletionShortcut(e)) { e.Handled = true; return; }
      if (TryHandleEvalShortcut(e)) { e.Handled = true; return; }
      if (HandleEditorChord(e)) { e.Handled = true; return; }
      if (_zoomCtrl.TryKey(e, _zoomPct, out var next)) { SetZoom(next); e.Handled = true; }
    }

    private bool TryHandleCompletionShortcut(KeyEventArgs e)
    {
      if (e.Key != Key.Space) return false;
      var mods = Keyboard.Modifiers;
      if ((mods & ModifierKeys.Control) == 0) return false;
      if ((mods & (ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows)) != 0) return false;
      ShowCompletion(allowEmptyPrefix: true);
      return true;
    }

    private void OnTextAreaTextEntering(object? sender, TextCompositionEventArgs e)
    {
      if (_completionWindow is null) return;
      if (string.IsNullOrEmpty(e.Text)) return;
      if (!IsWordChar(e.Text[0]))
        CloseCompletion();
    }

    private void OnTextAreaTextEntered(object? sender, TextCompositionEventArgs e)
    {
      if (_completionWindow is not null) return;
      if (string.IsNullOrEmpty(e.Text)) return;

      var ch = e.Text[0];
      if (!IsCompletionTriggerChar(ch)) return;

      var doc = Editor.Document;
      if (doc is null) return;
      if (IsInStringOrComment(doc, Editor.CaretOffset)) return;

      var offset = Editor.CaretOffset;
      if (offset <= 0)
      {
        ShowCompletion(allowEmptyPrefix: false);
        return;
      }

      var prev = offset >= 2 ? doc.GetCharAt(offset - 2) : ' ';
      if (IsWordChar(prev)) return;

      ShowCompletion(allowEmptyPrefix: false);
    }

    private static bool IsCompletionTriggerChar(char ch)
    {
      return char.IsLetter(ch) || ch == '_';
    }

    private static bool IsWordChar(char ch)
    {
      return char.IsLetterOrDigit(ch) || ch == '_' || ch == '\'';
    }

    private static bool IsInStringOrComment(TextDocument doc, int offset)
    {
      if (offset <= 0) return false;
      var text = doc.Text;
      offset = Math.Min(offset, text.Length);

      bool inString = false;
      bool inLineComment = false;
      int blockDepth = 0;

      for (int i = 0; i < offset; i++)
      {
        var c = text[i];
        var next = i + 1 < offset ? text[i + 1] : '\0';

        if (inLineComment)
        {
          if (c == '\n') inLineComment = false;
          continue;
        }

        if (blockDepth > 0)
        {
          if (c == '{' && next == '-')
          {
            blockDepth++;
            i++;
          }
          else if (c == '-' && next == '}')
          {
            blockDepth--;
            i++;
          }
          continue;
        }

        if (inString)
        {
          if (c == '\\' && i + 1 < offset)
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
          blockDepth = 1;
          i++;
          continue;
        }

        if (c == '"')
        {
          inString = true;
        }
      }

      return inString || inLineComment || blockDepth > 0;
    }

    private IReadOnlyList<TidalCompletionData> CompletionData =>
      _completionData ??= TidalCompletionCatalog.Items.Select(item => new TidalCompletionData(item)).ToList();

    private void ShowCompletion(bool allowEmptyPrefix)
    {
      var doc = Editor.Document;
      if (doc is null) return;
      if (IsInStringOrComment(doc, Editor.CaretOffset)) return;

      var start = GetWordStartOffset(Editor.CaretOffset);
      var prefix = doc.GetText(start, Math.Max(0, Editor.CaretOffset - start));
      if (string.IsNullOrEmpty(prefix) && !allowEmptyPrefix) return;

      CloseCompletion();

      var window = new ArchimedesCompletionWindow(Editor.TextArea)
      {
        CloseAutomatically = true,
        CloseWhenCaretAtBeginning = true,
        StartOffset = start,
        EndOffset = Editor.CaretOffset,
        SizeToContent = SizeToContent.Height
      };
      var owner = Window.GetWindow(this);
      if (owner is not null)
        window.Owner = owner;

      window.CompletionList.IsFiltering = true;
      foreach (var item in CompletionData)
        window.CompletionList.CompletionData.Add(item);

      ApplyCompletionStyle(window);
      window.Closed += (_, __) => _completionWindow = null;
      _completionWindow = window;
      window.Show();

      if (!string.IsNullOrEmpty(prefix))
        window.CompletionList.SelectItem(prefix);
    }

    private void CloseCompletion()
    {
      if (_completionWindow is null) return;
      _completionWindow.Close();
      _completionWindow = null;
    }

    private void ApplyCompletionStyle(ArchimedesCompletionWindow window)
    {
      if (TryFindResource("CompletionListBoxStyle") is Style listStyle)
        window.CompletionList.ListBox.Style = listStyle;
      if (TryFindResource("CompletionListItemStyle") is Style itemStyle)
        window.CompletionList.ListBox.ItemContainerStyle = itemStyle;
      if (TryFindResource("CompletionItemTemplate") is DataTemplate template)
        window.CompletionList.ListBox.ItemTemplate = template;

      if (TryFindResource("Brush.Completion.Panel") is Brush panelBrush)
        window.Background = panelBrush;
      else
        window.Background = Brushes.Black;
      window.AllowsTransparency = false;
      window.WindowStyle = WindowStyle.None;
      window.ShowInTaskbar = false;
    }

    private int GetWordStartOffset(int offset)
    {
      var doc = Editor.Document;
      if (doc is null) return offset;

      var start = Math.Clamp(offset, 0, doc.TextLength);
      while (start > 0 && IsWordChar(doc.GetCharAt(start - 1)))
        start--;

      return start;
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
        if (Editor.Document is not null)
          Editor.Document.Changed -= OnDocumentChanged;
        _diagnosticTimer.Stop();
        _diagnosticTimer.Tick -= OnDiagnosticTimerTick;
        _evalFlashTimer.Stop();
        _evalFlashTimer.Tick -= OnEvalFlashTimerTick;
        _bufferSaveTimer.Stop();
        _bufferSaveTimer.Tick -= OnBufferSaveTimerTick;
        _bufferStore.Save(Editor.Text);
        GoToLineBox.PreviewKeyDown -= OnGoToLinePreviewKeyDown;
        Editor.TextArea.PreviewKeyDown -= OnEditorPreviewKeyDown;
        Editor.TextArea.TextEntering -= OnTextAreaTextEntering;
        Editor.TextArea.TextEntered -= OnTextAreaTextEntered;
        Editor.PreviewMouseWheel -= OnEditorPreviewMouseWheel;
        CloseCompletion();
        WrapIndicator.Editor = null;
        Editor.TextArea.TextView.BackgroundRenderers.Remove(_diagnosticRenderer);
        _diagnosticRenderer.Editor = null;
        Editor.TextArea.TextView.BackgroundRenderers.Remove(_patternFlashRenderer);
        _patternFlashRenderer.Editor = null;
        Editor.TextArea.TextView.BackgroundRenderers.Remove(_evalFlashRenderer);
        _evalFlashRenderer.Editor = null;
        if (_moveLineHandler is not null)
        {
          Editor.TextArea.PopStackedInputHandler(_moveLineHandler);
          _moveLineHandler = null;
        }
        _editorWired = false;
      }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    public void ApplyTheme()
    {
      _themeApplier.Apply(Editor, _tidal ?? Editor.SyntaxHighlighting);
      UpdateWrapIndicatorBrush();
      UpdateDiagnosticsBrush();
      UpdatePatternFlashBrush();
      UpdateEvalFlashBrush();
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
      UpdateCaretText();
      UpdateGoToLineBox();
    }
    private void OnSelectionChanged(object? sender, EventArgs e) => UpdateSelectionText();

    public void SetEvalStatus(string text) => EvalText.Text = text;

    private void UpdateCaretText()
    {
      CaretText.Text = $"Ln: {Editor.TextArea.Caret.Line}, Col: {Editor.TextArea.Caret.Column}";
    }

    private void UpdateSelectionText()
    {
      SelText.Text = $"Sel: {Editor.SelectionLength}";
    }

    private bool TryHandleEvalShortcut(KeyEventArgs e)
    {
      if (e.Key is not (Key.Enter or Key.Return)) return false;

      var shift = IsShiftDown();
      var ctrl = IsCtrlDown();
      var alt = IsAltDown();

      if (shift && !ctrl && !alt)
      {
        return TryEvalRegion(GetSelectionOrCurrentLineRegion());
      }

      if (ctrl && !shift && !alt)
      {
        return TryEvalRegion(GetSelectionOrParagraphRegion());
      }

      return false;
    }


    private static bool IsCtrlDown()
    {
      return Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
    }

    private static bool IsAltDown()
    {
      return Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);
    }

    private static bool IsShiftDown()
    {
      return Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
    }

    private sealed class MoveLineInputHandler : TextAreaStackedInputHandler
    {
      private readonly Action<int> _moveLines;

      public MoveLineInputHandler(TextArea textArea, Action<int> moveLines) : base(textArea)
      {
        _moveLines = moveLines;
      }

      public override void OnPreviewKeyDown(KeyEventArgs e)
      {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Alt) == 0) return;
        if ((mods & (ModifierKeys.Control | ModifierKeys.Shift)) != 0) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Up) { _moveLines(-1); e.Handled = true; }
        else if (key == Key.Down) { _moveLines(1); e.Handled = true; }
      }
    }

    private bool TryEvalRegion(EvalRegion? region)
    {
      if (region is null) return true;
      var value = region.Value;
      if (string.IsNullOrWhiteSpace(value.Text)) return true;

      CaptureEvalSnapshot();
      FlashEvalRegion(value);
      var evalText = value.Text;
      var doc = Editor.Document;
      if (doc is not null)
      {
        var line = doc.GetLineByOffset(value.Offset);
        var startLine = line.LineNumber - 1;
        var startColumn = value.Offset - line.Offset;
        // Inject deltaContext offsets so Tidal can emit absolute OSC highlights.
        evalText = ApplyDeltaMini(evalText, startColumn, startLine);
      }
      RequestEval(evalText);
      return true;
    }

    private void CaptureEvalSnapshot()
    {
      var doc = Editor.Document;
      if (doc is null) return;
      _evalSnapshot = new EvalSnapshot(doc.Text);
      _evalChanges.Clear();
    }

    private void RequestEval(string? code)
    {
      if (string.IsNullOrWhiteSpace(code)) return;
      EvalRequested?.Invoke(this, code);
    }

    private void MoveLines(int direction)
    {
      var doc = Editor.Document;
      if (doc is null) return;

      var range = GetSelectedLineRange();
      if (range is null) return;

      var startLine = range.Value.start;
      var endLine = range.Value.end;
      var startLineNumber = startLine.LineNumber;
      var endLineNumber = endLine.LineNumber;
      if (direction < 0 && startLine.PreviousLine is null) return;
      if (direction > 0 && endLine.NextLine is null) return;

      var blockStart = startLine.Offset;
      var blockLength = GetBlockLength(startLine, endLine);
      var caretColumn = Editor.TextArea.Caret.Column;
      var hadSelection = Editor.SelectionLength > 0;

      using (doc.RunUpdate())
      {
        if (direction < 0)
        {
          var prev = startLine.PreviousLine!;
          var blockText = doc.GetText(blockStart, blockLength);
          if (endLine.DelimiterLength == 0)
          {
            var prevContent = doc.GetText(prev.Offset, prev.Length);
            var prevDelimiter = doc.GetText(prev.EndOffset, prev.DelimiterLength);
            doc.Remove(prev.Offset, prev.TotalLength + blockLength);
            doc.Insert(prev.Offset, blockText + prevDelimiter + prevContent);
          }
          else
          {
            var prevText = doc.GetText(prev.Offset, prev.TotalLength);
            doc.Remove(prev.Offset, prev.TotalLength + blockLength);
            doc.Insert(prev.Offset, blockText + prevText);
          }
        }
        else
        {
          var next = endLine.NextLine!;
          var blockText = doc.GetText(blockStart, blockLength);
          if (next.DelimiterLength == 0)
          {
            var blockDelimiter = doc.GetText(endLine.EndOffset, endLine.DelimiterLength);
            var blockCore = blockDelimiter.Length > 0 && blockText.Length >= blockDelimiter.Length
              ? blockText[..^blockDelimiter.Length]
              : blockText;
            var nextContent = doc.GetText(next.Offset, next.Length);
            doc.Remove(blockStart, blockLength + next.Length);
            doc.Insert(blockStart, nextContent + blockDelimiter + blockCore);
          }
          else
          {
            var nextText = doc.GetText(next.Offset, next.TotalLength);
            doc.Remove(blockStart, blockLength + next.TotalLength);
            doc.Insert(blockStart, nextText + blockText);
          }
        }
      }

      var newStartLine = startLineNumber + direction;
      var newEndLine = endLineNumber + direction;
      RestoreSelection(newStartLine, newEndLine, caretColumn, hadSelection);
    }

    private static int GetBlockLength(DocumentLine start, DocumentLine end)
    {
      return end.EndOffset + end.DelimiterLength - start.Offset;
    }

    private void RestoreSelection(int startLineNumber, int endLineNumber, int caretColumn, bool hadSelection)
    {
      var doc = Editor.Document;
      if (doc is null) return;

      var start = doc.GetLineByNumber(startLineNumber);
      var end = doc.GetLineByNumber(endLineNumber);

      if (hadSelection)
      {
        var length = Math.Max(0, end.EndOffset - start.Offset);
        Editor.Select(start.Offset, length);
      }

      var lineLength = end.Length;
      Editor.TextArea.Caret.Line = startLineNumber;
      Editor.TextArea.Caret.Column = Math.Max(1, Math.Min(caretColumn, lineLength + 1));
      Editor.TextArea.Caret.BringCaretToView();
    }

    private void FlashEvalRegion(EvalRegion region)
    {
      var doc = Editor.Document;
      if (doc is null) return;

      var offset = Math.Clamp(region.Offset, 0, doc.TextLength);
      var length = Math.Max(0, Math.Min(region.Length, doc.TextLength - offset));
      if (length <= 0) return;

      _evalFlashRenderer.AddFlash(offset, length);
      _evalFlashTimer.Start();
    }

    public void FlashPatternSpan(int startColumn, int startLine, int endColumn, int endLine, TimeSpan? duration = null)
    {
      var doc = Editor.Document;
      if (doc is null) return;
      if (_patternEditGate.ElapsedMilliseconds < PatternEditHoldMs) return;
      if (startLine <= 0 || endLine <= 0) { startLine++; endLine++; }

      startColumn = Math.Max(1, startColumn + 1);
      endColumn = Math.Max(1, endColumn + 1);

      if (!TryMapOffsets(doc, startLine, startColumn, endLine, endColumn, out var startOffset, out var endOffset))
        return;

      if (startOffset < doc.TextLength && doc.GetCharAt(startOffset) == '"')
        startOffset = Math.Min(doc.TextLength, startOffset + 1);

      if (endOffset > 0 && endOffset <= doc.TextLength && doc.GetCharAt(endOffset - 1) == '"')
        endOffset = Math.Max(startOffset + 1, endOffset - 1);

      if (endOffset <= startOffset)
        endOffset = Math.Min(doc.TextLength, startOffset + 1);

      var length = Math.Max(1, Math.Min(doc.TextLength - startOffset, endOffset - startOffset));
      if (length <= 0) return;

      _patternFlashRenderer.AddFlash(startOffset, length, duration);
      _evalFlashTimer.Start();
    }

    private bool TryMapOffsets(TextDocument doc, int startLine, int startColumn, int endLine, int endColumn,
      out int startOffset, out int endOffset)
    {
      startOffset = 0;
      endOffset = 0;

      if (_evalSnapshot is null || _evalChanges.Count == 0)
      {
        if (!TryGetOffset(doc, startLine, startColumn, out startOffset)) return false;
        if (!TryGetOffset(doc, endLine, endColumn, out endOffset)) return false;
        return true;
      }

      if (!TryGetOffset(_evalSnapshot.Document, startLine, startColumn, out var startOld)) return false;
      if (!TryGetOffset(_evalSnapshot.Document, endLine, endColumn, out var endOld)) return false;

      if (!TryMapSpanToCurrent(startOld, endOld, doc.TextLength, out startOffset, out endOffset))
        return false;
      return true;
    }

    private bool TryMapSpanToCurrent(int start, int end, int maxLen, out int mappedStart, out int mappedEnd)
    {
      mappedStart = start;
      mappedEnd = end;
      foreach (var change in _evalChanges)
      {
        if (change.Removed > 0)
        {
          var delStart = change.Offset;
          var delEnd = change.Offset + change.Removed;
          if (mappedStart < delEnd && mappedEnd > delStart)
            return false;
        }

        var delta = change.Inserted - change.Removed;
        if (mappedStart >= change.Offset) mappedStart += delta;
        if (mappedEnd >= change.Offset) mappedEnd += delta;
      }

      mappedStart = Math.Clamp(mappedStart, 0, maxLen);
      mappedEnd = Math.Clamp(mappedEnd, 0, maxLen);
      if (mappedEnd <= mappedStart) return false;
      return true;
    }

    private EvalRegion? GetSelectionOrCurrentLineRegion()
    {
      return GetSelectionRegion() ?? GetCurrentLineRegion();
    }

    private EvalRegion? GetSelectionOrParagraphRegion()
    {
      return GetSelectionRegion() ?? GetCurrentParagraphRegion();
    }

    private EvalRegion? GetSelectionRegion()
    {
      if (Editor.SelectionLength == 0) return null;
      return new EvalRegion(Editor.SelectionStart, Editor.SelectionLength, Editor.SelectedText);
    }

    private EvalRegion? GetCurrentLineRegion()
    {
      var doc = Editor.Document;
      if (doc is null) return null;
      var line = doc.GetLineByOffset(Editor.CaretOffset);
      return new EvalRegion(line.Offset, line.Length, doc.GetText(line));
    }

    private EvalRegion? GetCurrentParagraphRegion()
    {
      var doc = Editor.Document;
      if (doc is null) return null;

      var caretLine = doc.GetLineByOffset(Editor.CaretOffset);
      if (IsBlankLine(doc, caretLine)) return null;

      var start = caretLine;
      while (start.PreviousLine is not null && !IsBlankLine(doc, start.PreviousLine))
        start = start.PreviousLine;

      var end = caretLine;
      while (end.NextLine is not null && !IsBlankLine(doc, end.NextLine))
        end = end.NextLine;

      var startOffset = start.Offset;
      var endOffset = end.EndOffset;
      if (endOffset <= startOffset) return null;
      var length = endOffset - startOffset;
      return new EvalRegion(startOffset, length, doc.GetText(startOffset, length));
    }

    private static bool IsBlankLine(TextDocument doc, DocumentLine line)
    {
      return string.IsNullOrWhiteSpace(doc.GetText(line));
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
      ScheduleDiagnostics();
      ScheduleBufferSave();
      UpdateGoToLineBox();
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
      if (_evalSnapshot is null) return;
      _evalChanges.Add(new DocumentChange(e.Offset, e.RemovalLength, e.InsertionLength));
      _patternEditGate.Restart();
      if (_evalChanges.Count > 512)
      {
        _evalSnapshot = null;
        _evalChanges.Clear();
      }
    }

    private bool HandleEditorChord(KeyEventArgs e)
    {
      var mods = Keyboard.Modifiers;
      var shift = (mods & ModifierKeys.Shift) != 0;
      var ctrl = (mods & ModifierKeys.Control) != 0;

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

    private void ScheduleDiagnostics()
    {
      _diagnosticTimer.Stop();
      _diagnosticTimer.Start();
    }

    private void OnDiagnosticTimerTick(object? sender, EventArgs e)
    {
      _diagnosticTimer.Stop();
      UpdateDiagnostics();
    }

    private void OnBufferSaveTimerTick(object? sender, EventArgs e)
    {
      _bufferSaveTimer.Stop();
      if (_loadingBuffer) return;
      _bufferStore.Save(Editor.Text);
    }

    private void ScheduleBufferSave()
    {
      if (_loadingBuffer) return;
      _bufferSaveTimer.Stop();
      _bufferSaveTimer.Start();
    }

    private void LoadEditorBuffer()
    {
      var text = _bufferStore.Load();
      if (string.IsNullOrEmpty(text)) return;

      _loadingBuffer = true;
      try
      {
        Editor.Text = text;
      }
      finally
      {
        _loadingBuffer = false;
      }
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

    private void UpdateEvalFlashBrush()
    {
      var brush = Application.Current?.TryFindResource("Brush.Acc.Main") as Brush;
      _evalFlashRenderer.UpdateBrush(brush);
    }

    private void UpdatePatternFlashBrush()
    {
      var brush = Application.Current?.TryFindResource("Brush.Acc.PatternHighlight") as Brush;
      _patternFlashRenderer.UpdateBrush(brush);
    }

    private void OnEvalFlashTimerTick(object? sender, EventArgs e)
    {
      var evalActive = _evalFlashRenderer.PruneExpired();
      var patternActive = _patternFlashRenderer.PruneExpired();
      if (!evalActive && !patternActive)
        _evalFlashTimer.Stop();
    }

    private static bool TryGetOffset(TextDocument doc, int line, int column, out int offset)
    {
      offset = 0;
      if (doc.LineCount == 0) return false;

      line = Math.Max(1, Math.Min(line, doc.LineCount));
      var docLine = doc.GetLineByNumber(line);
      column = Math.Max(1, Math.Min(column, docLine.Length + 1));
      offset = docLine.Offset + column - 1;
      return true;
    }


    private static string ApplyDeltaMini(string code, int startColumn, int startLine)
    {
      if (string.IsNullOrEmpty(code)) return code;
      code = code.Replace("\r\n", "\n").Replace("\r", "\n");
      var sb = new StringBuilder(code.Length + 32);

      var column = Math.Max(0, startColumn);
      var line = Math.Max(0, startLine);
      var i = 0;

      while (i < code.Length)
      {
        var c = code[i];
        if (c == '"')
        {
          sb.Append("(deltaContext ").Append(column).Append(' ').Append(line).Append(" \"");
          i++;
          column++;

          while (i < code.Length)
          {
            c = code[i];
            if (c == '"')
            {
              sb.Append('"').Append(')');
              i++;
              column++;
              break;
            }

            sb.Append(c);
            if (c == '\n') { line++; column = 0; }
            else { column++; }
            i++;
          }

          continue;
        }

        sb.Append(c);
        if (c == '\n') { line++; column = 0; }
        else { column++; }
        i++;
      }

      return sb.ToString();
    }

    private readonly struct EvalRegion
    {
      public EvalRegion(int offset, int length, string text)
      {
        Offset = offset;
        Length = length;
        Text = text;
      }

      public int Offset { get; }
      public int Length { get; }
      public string Text { get; }
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

    private readonly struct DocumentChange
    {
      public DocumentChange(int offset, int removed, int inserted)
      {
        Offset = offset;
        Removed = removed;
        Inserted = inserted;
      }

      public int Offset { get; }
      public int Removed { get; }
      public int Inserted { get; }
    }

    private sealed class EvalSnapshot
    {
      public EvalSnapshot(string text)
      {
        Document = new TextDocument(text);
      }

      public TextDocument Document { get; }
    }
  }
}
