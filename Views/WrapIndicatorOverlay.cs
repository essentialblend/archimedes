// Views/WrapIndicatorOverlay.cs

using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

namespace archimedes.Views
{
  internal sealed class WrapIndicatorOverlay : FrameworkElement
  {
    private TextEditor? _editor;
    private TextView? _textView;
    private Brush? _brush;
    private Pen? _pen;

    public bool Enabled { get; set; } = true;

    public TextEditor? Editor
    {
      get => _editor;
      set
      {
        if (ReferenceEquals(_editor, value)) return;
        Detach();
        _editor = value;
        Attach();
      }
    }

    public void UpdateBrush(Brush? brush)
    {
      _brush = brush;
      _pen = null;
      InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
      base.OnRender(drawingContext);

      if (!Enabled || _textView is null || _editor is null) return;
      if (!_textView.VisualLinesValid) return;

      var rightPad = _editor.Padding.Right;
      if (rightPad <= 0) return;

      var brush = _brush ??
                  (Brush?)Application.Current?.TryFindResource("Brush.Text.Dim") ??
                  Brushes.Gray;

      _pen ??= CreatePen(brush);

      Point origin;
      try
      {
        origin = _textView.TranslatePoint(new Point(0, 0), this);
      }
      catch (InvalidOperationException)
      {
        return;
      }

      foreach (var visualLine in _textView.VisualLines)
      {
        if (visualLine.TextLines.Count <= 1) continue;

        for (int i = 0; i < visualLine.TextLines.Count - 1; i++)
        {
          var textLine = visualLine.TextLines[i];
          DrawWrapIndicator(drawingContext, origin, visualLine, textLine, rightPad);
        }
      }
    }

    private void DrawWrapIndicator(DrawingContext drawingContext, Point origin, VisualLine visualLine, TextLine textLine, double rightPad)
    {
      var lineTop = origin.Y + visualLine.GetTextLineVisualYPosition(textLine, VisualYPosition.TextTop) - _textView!.VerticalOffset;
      var baseline = lineTop + textLine.Baseline - 1;

      var baseSize = Math.Max(6, textLine.Height * 0.45);
      var head = baseSize * 0.35;
      var horiz = baseSize;
      var vert = baseSize * 0.55;
      var total = horiz + head;

      var available = rightPad - 4;
      if (available <= 0) return;

      var scale = Math.Min(1.0, available / total);
      horiz *= scale;
      head *= scale;
      vert *= scale;
      total *= scale;

      var areaStart = ActualWidth - rightPad;
      var drawX = areaStart + Math.Max(2, (rightPad - total) / 2);

      var p1 = new Point(drawX, baseline);
      var p2 = new Point(drawX + horiz, baseline);
      var p3 = new Point(p2.X, baseline + vert);

      drawingContext.DrawLine(_pen!, p1, p2);
      drawingContext.DrawLine(_pen!, p2, p3);
      drawingContext.DrawLine(_pen!, p3, new Point(p3.X - head, p3.Y - head));
      drawingContext.DrawLine(_pen!, p3, new Point(p3.X - head, p3.Y + head));
    }

    private void Attach()
    {
      if (_editor is null) return;
      _textView = _editor.TextArea.TextView;

      _textView.VisualLinesChanged += OnTextViewChanged;
      _textView.ScrollOffsetChanged += OnTextViewChanged;
      _editor.SizeChanged += OnTextViewChanged;

      InvalidateVisual();
    }

    private void Detach()
    {
      if (_textView is not null)
      {
        _textView.VisualLinesChanged -= OnTextViewChanged;
        _textView.ScrollOffsetChanged -= OnTextViewChanged;
      }

      if (_editor is not null)
        _editor.SizeChanged -= OnTextViewChanged;

      _textView = null;
    }

    private void OnTextViewChanged(object? sender, EventArgs e) => InvalidateVisual();

    private static Pen CreatePen(Brush brush)
    {
      var pen = new Pen(brush, 1);
      pen.Freeze();
      return pen;
    }
  }
}
