using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ComicViewer;

public partial class MainWindow
{
    private void ImageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePageWidth(e.NewSize.Width);

    private void ImageScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_isLoadingArchive) RefreshReaderViewport();
    }

    private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        CancelPageNumberInput();
        if (GetPagesScrollViewer() is not { } scrollViewer) return;
        var lines = SystemParameters.WheelScrollLines > 0 ? SystemParameters.WheelScrollLines : 3;
        var pixels = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine * lines * MouseWheelPixelsPerLine * MouseWheelScrollMultiplier;
        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - pixels);
        e.Handled = true;
    }

    private void NavigateByPageOffset(int offset)
    {
        if (Pages.Count == 0) return;
        CancelPageNumberInput();
        ScrollPageToTop(Math.Clamp(GetVisiblePageIndexes()[0] + offset, 0, Pages.Count - 1));
    }

    private void UpdatePageWidth(double fallbackWidth = 0)
    {
        var width = GetPagesScrollViewer()?.ViewportWidth ?? 0;
        if (!double.IsFinite(width) || width <= 1) width = fallbackWidth > 1 ? fallbackWidth : PagesListBox.ActualWidth;
        width = Math.Max(1, width);
        if (Math.Abs(width - PageWidth) <= 0.1) return;
        PageWidth = width;
        if (!_isLoadingArchive) RefreshReaderViewport();
    }

    private ScrollViewer? GetPagesScrollViewer()
    {
        if (_pagesScrollViewer is not null) return _pagesScrollViewer;
        PagesListBox.ApplyTemplate();
        return _pagesScrollViewer = FindVisualChildren<ScrollViewer>(PagesListBox).FirstOrDefault();
    }

    private void RefreshReaderViewport()
    {
        if (_images is null || Pages.Count == 0 || _closing) return;
        var visible = GetVisiblePageIndexes();
        _currentPageIndex = visible[0];
        UpdateReadingStatus(_currentPageIndex);
        var targets = visible.Concat(visible.SelectMany(index => new[] { index - 1, index + 1 })
            .Where(index => index >= 0 && index < Pages.Count)).Distinct();
        var decodeWidth = Math.Max(512, (int)Math.Ceiling(PageWidth * VisualTreeHelper.GetDpi(this).DpiScaleX / 128) * 128);
        _images.Refresh(targets, decodeWidth);
    }

    // Only realized containers are inspected; WPF already owns the layout and scroll position.
    private int[] GetVisiblePageIndexes()
    {
        var indexes = new SortedSet<int>();
        if (GetPagesScrollViewer() is { } scrollViewer)
        {
            foreach (var container in FindVisualChildren<ListBoxItem>(PagesListBox))
            {
                var index = PagesListBox.ItemContainerGenerator.IndexFromContainer(container);
                if (index < 0 || index >= Pages.Count || container.ActualHeight <= 0) continue;
                var bounds = container.TransformToAncestor(scrollViewer).TransformBounds(new Rect(container.RenderSize));
                if (bounds.Bottom > 0 && bounds.Top < scrollViewer.ViewportHeight) indexes.Add(index);
            }
        }
        if (indexes.Count == 0 && Pages.Count > 0) indexes.Add(Math.Clamp(_currentPageIndex, 0, Pages.Count - 1));
        return indexes.ToArray();
    }

    private void ScrollPageToTop(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;
        var reader = _reader;
        PagesListBox.ScrollIntoView(Pages[pageIndex]);
        Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(reader, _reader) || pageIndex >= Pages.Count || _closing) return;
            PagesListBox.UpdateLayout();
            if (GetPagesScrollViewer() is { } scrollViewer
                && PagesListBox.ItemContainerGenerator.ContainerFromIndex(pageIndex) is FrameworkElement container)
            {
                var top = container.TransformToAncestor(scrollViewer).Transform(new Point()).Y;
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + top);
            }
            _currentPageIndex = pageIndex;
            RefreshReaderViewport();
        }, DispatcherPriority.Loaded);
    }

    private void UpdateReadingStatus(int currentPageIndex)
    {
        if (_archivePath is null || Pages.Count == 0)
        {
            ClearReadingProgress();
            return;
        }

        StatusTextBlock.Text = Path.GetFileName(_archivePath);
        CurrentPageTextBox.IsEnabled = true;
        if (!CurrentPageTextBox.IsKeyboardFocusWithin)
        {
            CurrentPageTextBox.Text = (currentPageIndex + 1).ToString(CultureInfo.InvariantCulture);
        }

        TotalPagesTextBlock.Text = Pages.Count.ToString(CultureInfo.InvariantCulture);
        LoadedRangeTextBlock.Text = _reader?.LoadedRange ?? "0";
    }

    private void ClearReadingProgress()
    {
        CurrentPageTextBox.IsEnabled = false;
        CurrentPageTextBox.Text = "";
        TotalPagesTextBlock.Text = "0";
        LoadedRangeTextBlock.Text = "0";
    }

    private void CurrentPageTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.OldFocus is { } oldFocus && !ReferenceEquals(oldFocus, CurrentPageTextBox))
        {
            _pageNumberPreviousFocus = oldFocus;
        }

        CurrentPageTextBox.SelectAll();
    }

    private void CurrentPageTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitPageNumberInput();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelPageNumberInput();
            e.Handled = true;
        }
    }

    private void CurrentPageTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_isCancelingPageNumberInput)
        {
            _isCancelingPageNumberInput = false;
            return;
        }

        CommitPageNumberInput();
    }

    private void CurrentPageTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void CommitPageNumberInput()
    {
        if (Pages.Count == 0)
        {
            ClearReadingProgress();
            return;
        }

        if (int.TryParse(CurrentPageTextBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pageNumber))
        {
            var targetIndex = Math.Clamp(pageNumber - 1, 0, Pages.Count - 1);
            CurrentPageTextBox.Text = (targetIndex + 1).ToString(CultureInfo.InvariantCulture);
            ScrollPageToTop(targetIndex);
        }
        else
        {
            UpdateReadingStatus(Math.Clamp(_currentPageIndex, 0, Pages.Count - 1));
        }
    }

    private void CancelPageNumberInput()
    {
        if (!CurrentPageTextBox.IsKeyboardFocusWithin)
        {
            return;
        }

        _isCancelingPageNumberInput = true;
        if (Pages.Count > 0)
        {
            CurrentPageTextBox.Text = (Math.Clamp(_currentPageIndex, 0, Pages.Count - 1) + 1).ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            CurrentPageTextBox.Text = "";
        }

        Keyboard.ClearFocus();
        RestorePageNumberPreviousFocus();
    }

    private void RestorePageNumberPreviousFocus()
    {
        var previousFocus = _pageNumberPreviousFocus;
        _pageNumberPreviousFocus = null;
        if (previousFocus is null || ReferenceEquals(previousFocus, CurrentPageTextBox))
        {
            return;
        }

        try
        {
            _ = Keyboard.Focus(previousFocus);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var nestedChild in FindVisualChildren<T>(child))
            {
                yield return nestedChild;
            }
        }
    }

}
