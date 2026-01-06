// Views/ArchimedesCompletionWindow.cs

using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace archimedes.Views
{
  internal sealed class ArchimedesCompletionWindow : CompletionWindowBase
  {
    private readonly CompletionList completionList = new();
    private readonly ContentControl detailHost = new();
    private const double DetailWidth = 620;
    private const double ListHeight = 320;

    public CompletionList CompletionList => completionList;

    public ArchimedesCompletionWindow(TextArea textArea) : base(textArea)
    {
      CloseAutomatically = true;
      SizeToContent = SizeToContent.Height;
      MaxHeight = 420;
      Width = 880;
      MinHeight = 15;
      MinWidth = 880;

      detailHost.IsHitTestVisible = true;
      detailHost.Focusable = false;
      Content = BuildLayout();

      ApplySizing();
      AttachEvents();
    }

    private UIElement BuildLayout()
    {
      completionList.Width = 220;
      completionList.MinWidth = 200;
      completionList.Height = ListHeight;
      completionList.MaxHeight = ListHeight;
      completionList.VerticalAlignment = VerticalAlignment.Top;

      var separator = new Border
      {
        Width = 1,
        Margin = new Thickness(0, 8, 0, 8)
      };
      separator.SetResourceReference(Border.BackgroundProperty, "Brush.Border.Light");

      detailHost.Margin = new Thickness(12, 0, 0, 0);
      detailHost.HorizontalAlignment = HorizontalAlignment.Left;
      detailHost.VerticalAlignment = VerticalAlignment.Top;
      detailHost.Width = DetailWidth;

      var grid = new Grid
      {
        Background = TryFindResource("Brush.Completion.Panel") as Brush ?? Brushes.Black
      };
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

      Grid.SetColumn(completionList, 0);
      Grid.SetColumn(separator, 1);
      Grid.SetColumn(detailHost, 2);

      grid.Children.Add(completionList);
      grid.Children.Add(separator);
      grid.Children.Add(detailHost);

      return grid;
    }

    private void ApplySizing()
    {
      detailHost.Height = ListHeight;
      detailHost.MaxHeight = ListHeight;
    }

    private void completionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      var item = completionList.SelectedItem;
      if (item is null)
      {
        detailHost.Content = BuildFallback();
        return;
      }

      var description = item.Description;
      if (description is string text)
      {
        detailHost.Content = new TextBlock
        {
          Text = text,
          TextWrapping = TextWrapping.Wrap,
          MaxWidth = 420,
          Foreground = GetTextBrush()
        };
      }
      else
      {
        detailHost.Content = description ?? BuildFallback();
      }
    }

    private Brush GetTextBrush()
    {
      return TryFindResource("Brush.Text.Main") as Brush ?? SystemColors.ControlTextBrush;
    }

    private UIElement BuildFallback()
    {
      return new TextBlock
      {
        Text = "No description available.",
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = DetailWidth,
        Foreground = TryFindResource("Brush.Text.Dim") as Brush ?? SystemColors.GrayTextBrush,
        FontStyle = FontStyles.Italic
      };
    }

    private void completionList_InsertionRequested(object? sender, EventArgs e)
    {
      Close();
      var item = completionList.SelectedItem;
      if (item is not null)
      {
        item.Complete(TextArea, new AnchorSegment(TextArea.Document, StartOffset, EndOffset - StartOffset), e);
      }
    }

    private void AttachEvents()
    {
      completionList.InsertionRequested += completionList_InsertionRequested;
      completionList.SelectionChanged += completionList_SelectionChanged;
      TextArea.Caret.PositionChanged += CaretPositionChanged;
      TextArea.MouseWheel += textArea_MouseWheel;
      TextArea.PreviewTextInput += textArea_PreviewTextInput;
    }

    protected override void DetachEvents()
    {
      completionList.InsertionRequested -= completionList_InsertionRequested;
      completionList.SelectionChanged -= completionList_SelectionChanged;
      TextArea.Caret.PositionChanged -= CaretPositionChanged;
      TextArea.MouseWheel -= textArea_MouseWheel;
      TextArea.PreviewTextInput -= textArea_PreviewTextInput;
      base.DetachEvents();
    }

    protected override void OnClosed(EventArgs e)
    {
      base.OnClosed(e);
      detailHost.Content = null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
      base.OnKeyDown(e);
      if (!e.Handled)
      {
        completionList.HandleKey(e);
      }
    }

    private void textArea_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
      e.Handled = RaiseEventPair(this, PreviewTextInputEvent, TextInputEvent,
        new TextCompositionEventArgs(e.Device, e.TextComposition));
    }

    private void textArea_MouseWheel(object sender, MouseWheelEventArgs e)
    {
      e.Handled = RaiseEventPair(GetScrollEventTarget(),
        PreviewMouseWheelEvent, MouseWheelEvent,
        new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta));
    }

    private UIElement GetScrollEventTarget()
    {
      return completionList.ScrollViewer as UIElement
        ?? completionList.ListBox as UIElement
        ?? completionList;
    }

    public bool CloseAutomatically { get; set; }

    protected override bool CloseOnFocusLost => CloseAutomatically;

    public bool CloseWhenCaretAtBeginning { get; set; }

    private void CaretPositionChanged(object? sender, EventArgs e)
    {
      var offset = TextArea.Caret.Offset;
      if (offset == StartOffset)
      {
        if (CloseAutomatically && CloseWhenCaretAtBeginning)
        {
          Close();
        }
        else
        {
          completionList.SelectItem(string.Empty);
        }
        return;
      }

      if (offset < StartOffset || offset > EndOffset)
      {
        if (CloseAutomatically) Close();
      }
      else
      {
        var document = TextArea.Document;
        if (document is not null)
        {
          completionList.SelectItem(document.GetText(StartOffset, offset - StartOffset));
        }
      }
    }
  }
}
