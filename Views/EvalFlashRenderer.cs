// Views/EvalFlashRenderer.cs

using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace archimedes.Views
{
  internal sealed class EvalFlashRenderer : IBackgroundRenderer
  {
    private sealed class Flash
    {
      public Flash(int offset, int length, DateTime startedUtc, TimeSpan? durationOverride)
      {
        Offset = offset;
        Length = length;
        StartedUtc = startedUtc;
        DurationOverride = durationOverride;
      }

      public int Offset { get; }
      public int Length { get; }
      public DateTime StartedUtc { get; }
      public TimeSpan? DurationOverride { get; }
    }

    private readonly List<Flash> _flashes = new();
    private Brush? _brush;

    public TextEditor? Editor { get; set; }
    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(450);
    public byte BaseAlpha { get; set; } = 90;
    public bool OutlineOnly { get; set; }
    public double OutlineThickness { get; set; } = 1.2;

    public KnownLayer Layer => KnownLayer.Selection;

    public void UpdateBrush(Brush? brush)
    {
      _brush = brush;
    }

    public void AddFlash(int offset, int length, TimeSpan? durationOverride = null)
    {
      if (length <= 0) return;

      _flashes.Add(new Flash(offset, length, DateTime.UtcNow, durationOverride));
      Editor?.TextArea.TextView.InvalidateLayer(Layer);
    }

    public bool PruneExpired()
    {
      if (_flashes.Count == 0) return false;

      var now = DateTime.UtcNow;
      for (int i = _flashes.Count - 1; i >= 0; i--)
      {
        if (now - _flashes[i].StartedUtc > GetDuration(_flashes[i]))
          _flashes.RemoveAt(i);
      }

      Editor?.TextArea.TextView.InvalidateLayer(Layer);
      return _flashes.Count > 0;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
      if (_flashes.Count == 0 || Editor?.Document is null) return;
      if (!textView.VisualLinesValid) return;

      var baseColor = GetBaseColor();
      var now = DateTime.UtcNow;

      foreach (var flash in _flashes)
      {
        var age = now - flash.StartedUtc;
        var duration = GetDuration(flash);
        if (duration.TotalMilliseconds <= 0) continue;
        if (age > duration) continue;

        var t = 1.0 - (age.TotalMilliseconds / duration.TotalMilliseconds);
        var alpha = (byte)Math.Clamp((int)(BaseAlpha * t), 0, 255);
        if (alpha == 0) continue;

        var brush = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
        brush.Freeze();

        DrawSegment(textView, drawingContext, flash, brush);
      }
    }

    private void DrawSegment(TextView textView, DrawingContext drawingContext, Flash flash, Brush brush)
    {
      var doc = Editor?.Document;
      if (doc is null) return;

      var offset = Math.Clamp(flash.Offset, 0, doc.TextLength);
      var length = Math.Max(1, Math.Min(flash.Length, doc.TextLength - offset));
      if (length <= 0) return;

      var segment = new TextSegment { StartOffset = offset, Length = length };
      foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
      {
        if (OutlineOnly)
        {
          var pen = new Pen(brush, OutlineThickness);
          pen.Freeze();
          drawingContext.DrawRectangle(null, pen, rect);
        }
        else
        {
          drawingContext.DrawRectangle(brush, null, rect);
        }
      }
    }

    private Color GetBaseColor()
    {
      if (_brush is SolidColorBrush scb) return scb.Color;
      if (Application.Current?.TryFindResource("Brush.Acc.Main") is SolidColorBrush res) return res.Color;
      return Colors.DeepSkyBlue;
    }

    private TimeSpan GetDuration(Flash flash) => flash.DurationOverride ?? Duration;
  }
}
