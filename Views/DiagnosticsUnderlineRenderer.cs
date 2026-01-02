// Views/DiagnosticsUnderlineRenderer.cs

using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace archimedes.Views
{
  // Draws small squiggles under diagnostic offsets to make issues discoverable.
  internal sealed class DiagnosticsUnderlineRenderer : IBackgroundRenderer
  {
    private IReadOnlyList<DiagnosticIssue> _issues = Array.Empty<DiagnosticIssue>();
    private Pen? _pen;
    private Brush? _brush;

    public TextEditor? Editor { get; set; }

    public KnownLayer Layer => KnownLayer.Selection;

    public void UpdateBrush(Brush? brush)
    {
      _brush = brush;
      _pen = null;
    }

    public void SetIssues(IReadOnlyList<DiagnosticIssue> issues)
    {
      _issues = issues;
      Editor?.TextArea.TextView.InvalidateLayer(Layer);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
      if (_issues.Count == 0 || Editor?.Document is null) return;
      if (!textView.VisualLinesValid) return;

      var brush = _brush ??
                  (Brush?)Application.Current?.TryFindResource("Brush.Acc.Red") ??
                  Brushes.Red;

      _pen ??= CreatePen(brush);

      foreach (var issue in _issues)
      {
        DrawSquiggle(textView, drawingContext, issue);
      }
    }

    private void DrawSquiggle(TextView textView, DrawingContext drawingContext, DiagnosticIssue issue)
    {
      var doc = Editor?.Document;
      if (doc is null) return;

      var offset = Math.Clamp(issue.Offset, 0, doc.TextLength);
      var length = Math.Max(1, issue.Length);
      var endOffset = Math.Min(doc.TextLength, offset + length);

      var startLoc = doc.GetLocation(offset);
      var endLoc = doc.GetLocation(endOffset);

      var startPos = new TextViewPosition(startLoc.Line, startLoc.Column);
      var endPos = new TextViewPosition(endLoc.Line, endLoc.Column);

      var startPoint = textView.GetVisualPosition(startPos, VisualYPosition.TextBottom);
      var endPoint = textView.GetVisualPosition(endPos, VisualYPosition.TextBottom);

      var scroll = textView.ScrollOffset;
      var x1 = startPoint.X - scroll.X;
      var y = startPoint.Y - scroll.Y + 1;
      var x2 = endPoint.Y == startPoint.Y ? endPoint.X - scroll.X : x1 + textView.WideSpaceWidth;

      DrawWavyLine(drawingContext, x1, x2, y);
    }

    private void DrawWavyLine(DrawingContext dc, double x1, double x2, double y)
    {
      var step = 4.0;
      var height = 2.0;

      if (x2 < x1)
        (x1, x2) = (x2, x1);

      var count = Math.Max(2, (int)Math.Ceiling((x2 - x1) / step));
      var geometry = new StreamGeometry();

      using (var ctx = geometry.Open())
      {
        ctx.BeginFigure(new Point(x1, y), false, false);
        for (int i = 1; i <= count; i++)
        {
          var x = x1 + i * step;
          var up = i % 2 == 0 ? -height : height;
          ctx.LineTo(new Point(x, y + up), true, false);
        }
      }

      geometry.Freeze();
      dc.DrawGeometry(null, _pen, geometry);
    }

    private static Pen CreatePen(Brush brush)
    {
      var pen = new Pen(brush, 1);
      pen.Freeze();
      return pen;
    }
  }
}
