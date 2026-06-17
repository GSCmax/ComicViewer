using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ComicViewer;

public partial class MainWindow
{
    private void ImageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        InvalidateViewportSnapshot();
        UpdatePageWidth(e.NewSize.Width);
    }

    private void ImageScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isLoadingArchive || Pages.Count == 0)
        {
            return;
        }

        _lastScrollUtc = DateTime.UtcNow;
        RefreshViewportSnapshot();
        var currentPageIndex = GetCurrentPageIndex();
        _currentPageIndex = currentPageIndex;
        UpdateReadingStatus(currentPageIndex);
        StartMediaLoading(currentPageIndex);
        RefreshDecodedImages();
    }

    private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        CancelPageNumberInput();
        var scrollViewer = GetPagesScrollViewer();
        if (scrollViewer is null)
        {
            return;
        }

        var wheelLines = SystemParameters.WheelScrollLines > 0 ? SystemParameters.WheelScrollLines : 3;
        var deltaSteps = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine;
        var scrollPixels = deltaSteps * wheelLines * MouseWheelPixelsPerLine * MouseWheelScrollMultiplier;
        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - scrollPixels);
        e.Handled = true;
    }

    private void UpdatePageWidth(double fallbackWidth = 0)
    {
        var previousDecodePixelWidth = GetDecodePixelWidth();
        var width = ResolvePageWidth(fallbackWidth);
        if (Math.Abs(width - _pageWidth) <= 0.1)
        {
            return;
        }

        _pageWidth = width;
        foreach (var page in Pages)
        {
            page.Resize(width);
        }
        InvalidateViewportSnapshot();

        if (Pages.Count > 0)
        {
            ClearDecodedImagesForWiderDecode(previousDecodePixelWidth, GetDecodePixelWidth());
            StartMediaLoading(GetCurrentPageIndex());
            RefreshDecodedImages();
        }
    }

    private double ResolvePageWidth(double fallbackWidth = 0)
    {
        var scrollViewer = GetPagesScrollViewer();
        var width = scrollViewer?.ViewportWidth ?? 0;
        if (double.IsNaN(width) || width <= 1)
        {
            width = fallbackWidth;
        }

        if (double.IsNaN(width) || width <= 1)
        {
            width = PagesListBox.ActualWidth;
        }

        if (double.IsNaN(width) || width <= 1)
        {
            width = ActualWidth;
        }

        return Math.Max(1, width);
    }

    private int GetDecodePixelWidth()
    {
        return Math.Max(MinimumDecodePixelWidth, (int)Math.Ceiling(_pageWidth * VisualTreeHelper.GetDpi(this).DpiScaleX));
    }

    private ScrollViewer? GetPagesScrollViewer()
    {
        if (_pagesScrollViewer is not null)
        {
            return _pagesScrollViewer;
        }

        PagesListBox.ApplyTemplate();
        _pagesScrollViewer = FindVisualChild<ScrollViewer>(PagesListBox);
        return _pagesScrollViewer;
    }

    private int GetCurrentPageIndex()
    {
        EnsureViewportSnapshot();
        return TryGetFirstVisiblePageIndex() ?? GetPageIndexAtOffset(GetPagesScrollViewer()?.VerticalOffset ?? 0);
    }

    private int GetPageIndexAtOffset(double offset)
    {
        var top = 0d;
        for (var i = 0; i < Pages.Count; i++)
        {
            var pageHeight = Math.Max(1, Pages[i].DisplayHeight);
            if (offset < top + pageHeight)
            {
                return i;
            }

            top += pageHeight;
        }

        return Math.Max(0, Pages.Count - 1);
    }

    private void ScrollPageToTop(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count)
        {
            return;
        }

        PagesListBox.ScrollIntoView(Pages[pageIndex]);
        Dispatcher.BeginInvoke((Action)(() =>
        {
            InvalidateViewportSnapshot();
            AlignRealizedPageToTop(pageIndex);
            _currentPageIndex = pageIndex;
            UpdateReadingStatus(pageIndex);
            StartMediaLoading(pageIndex);
            RefreshDecodedImages();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void AlignRealizedPageToTop(int pageIndex)
    {
        var scrollViewer = GetPagesScrollViewer();
        if (scrollViewer is null)
        {
            return;
        }

        PagesListBox.UpdateLayout();
        if (PagesListBox.ItemContainerGenerator.ContainerFromIndex(pageIndex) is not FrameworkElement container)
        {
            return;
        }

        try
        {
            var bounds = container.TransformToAncestor(scrollViewer)
                .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + bounds.Top);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private int? TryGetFirstVisiblePageIndex()
    {
        EnsureViewportSnapshot();
        return _firstVisiblePageIndexSnapshot;
    }

    private HashSet<int> GetRealizedPageIndexes()
    {
        EnsureViewportSnapshot();
        return new HashSet<int>(_realizedPageIndexesSnapshot);
    }

    private HashSet<int> GetVisiblePageIndexes(int adjacentPages)
    {
        EnsureViewportSnapshot();
        var indexes = new HashSet<int>(_visiblePageIndexesSnapshot);
        AddAdjacentPageIndexes(indexes, adjacentPages);
        return indexes;
    }

    private void EnsureViewportSnapshot()
    {
        if (_viewportSnapshotDirty)
        {
            RefreshViewportSnapshot();
        }
    }

    private void InvalidateViewportSnapshot()
    {
        _viewportSnapshotDirty = true;
    }

    private void RefreshViewportSnapshot()
    {
        var realizedIndexes = new HashSet<int>();
        var visibleIndexes = new HashSet<int>();
        int? firstVisibleIndex = null;
        var bestTop = double.PositiveInfinity;
        var scrollViewer = GetPagesScrollViewer();
        if (scrollViewer is not null && Pages.Count > 0)
        {
            foreach (var container in FindVisualChildren<ListBoxItem>(PagesListBox))
            {
                var index = PagesListBox.ItemContainerGenerator.IndexFromContainer(container);
                if (index < 0 || index >= Pages.Count)
                {
                    continue;
                }

                realizedIndexes.Add(index);
                if (container.ActualHeight <= 0)
                {
                    continue;
                }

                Rect bounds;
                try
                {
                    bounds = container.TransformToAncestor(scrollViewer)
                        .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (bounds.Bottom <= 0 || bounds.Top >= scrollViewer.ViewportHeight)
                {
                    continue;
                }

                visibleIndexes.Add(index);
                if (bounds.Top < bestTop)
                {
                    bestTop = bounds.Top;
                    firstVisibleIndex = index;
                }
            }
        }

        if (visibleIndexes.Count == 0 && Pages.Count > 0)
        {
            var fallbackIndex = Math.Clamp(_currentPageIndex, 0, Pages.Count - 1);
            visibleIndexes.Add(fallbackIndex);
        }

        _realizedPageIndexesSnapshot = realizedIndexes;
        _visiblePageIndexesSnapshot = visibleIndexes;
        _firstVisiblePageIndexSnapshot = firstVisibleIndex;
        _viewportSnapshotDirty = false;
    }

    private void AddAdjacentPageIndexes(HashSet<int> indexes, int adjacentPages)
    {
        if (adjacentPages <= 0 || Pages.Count == 0)
        {
            return;
        }

        var visible = indexes.ToArray();
        foreach (var index in visible)
        {
            for (var offset = 1; offset <= adjacentPages; offset++)
            {
                if (index - offset >= 0)
                {
                    indexes.Add(index - offset);
                }

                if (index + offset < Pages.Count)
                {
                    indexes.Add(index + offset);
                }
            }
        }
    }

    private IEnumerable<int> EnumeratePagesFrom(int currentPageIndex)
    {
        return Enumerable.Range(0, Pages.Count)
            .OrderBy(index => GetReadingDistance(index, currentPageIndex))
            .ThenBy(index => Math.Abs(index - currentPageIndex));
    }

    private static double GetReadingDistance(int pageIndex, int currentPageIndex)
    {
        var offset = pageIndex - currentPageIndex;
        return offset >= 0 ? offset / 2d : -offset;
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
        LoadedRangeTextBlock.Text = GetLoadedRangeText();
    }

    private string GetLoadedRangeText()
    {
        var loaded = Pages
            .Where(page => page.HasEncodedDisplayImageData || (page.IsVideo && page.IsLoaded))
            .Select(page => page.Index + 1)
            .ToList();
        if (loaded.Count == 0)
        {
            return "0";
        }

        return loaded.Count == 1
            ? loaded[0].ToString(CultureInfo.InvariantCulture)
            : $"{loaded.Min()}-{loaded.Max()}";
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
    }

    private static string FormatByteSize(long bytes)
    {
        return bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024d / 1024d / 1024d:0.0}GB"
            : $"{bytes / 1024d / 1024d:0.0}MB";
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

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var nestedChild = FindVisualChild<T>(child);
            if (nestedChild is not null)
            {
                return nestedChild;
            }
        }

        return null;
    }
}
