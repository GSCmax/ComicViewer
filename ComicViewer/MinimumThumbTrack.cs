using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ComicViewer;

public sealed class MinimumThumbTrack : Track
{
    public static readonly DependencyProperty MinimumThumbLengthProperty =
        DependencyProperty.Register(
            nameof(MinimumThumbLength),
            typeof(double),
            typeof(MinimumThumbTrack),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsArrange));

    public double MinimumThumbLength
    {
        get => (double)GetValue(MinimumThumbLengthProperty);
        set => SetValue(MinimumThumbLengthProperty, value);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = base.ArrangeOverride(arrangeSize);
        if (Thumb is null || MinimumThumbLength <= 0)
        {
            return size;
        }

        if (Orientation == System.Windows.Controls.Orientation.Vertical)
        {
            ArrangeVerticalThumb(arrangeSize);
        }
        else
        {
            ArrangeHorizontalThumb(arrangeSize);
        }

        return size;
    }

    private void ArrangeVerticalThumb(Size arrangeSize)
    {
        var targetHeight = Math.Min(MinimumThumbLength, arrangeSize.Height);
        if (Thumb.RenderSize.Height >= targetHeight || targetHeight <= 0)
        {
            return;
        }

        var top = Math.Clamp(
            VisualTreeHelper.GetOffset(Thumb).Y - (targetHeight - Thumb.RenderSize.Height) / 2,
            0,
            Math.Max(0, arrangeSize.Height - targetHeight));
        DecreaseRepeatButton?.Arrange(new Rect(0, 0, arrangeSize.Width, top));
        Thumb.Arrange(new Rect(0, top, arrangeSize.Width, targetHeight));
        IncreaseRepeatButton?.Arrange(new Rect(0, top + targetHeight, arrangeSize.Width, Math.Max(0, arrangeSize.Height - top - targetHeight)));
    }

    private void ArrangeHorizontalThumb(Size arrangeSize)
    {
        var targetWidth = Math.Min(MinimumThumbLength, arrangeSize.Width);
        if (Thumb.RenderSize.Width >= targetWidth || targetWidth <= 0)
        {
            return;
        }

        var left = Math.Clamp(
            VisualTreeHelper.GetOffset(Thumb).X - (targetWidth - Thumb.RenderSize.Width) / 2,
            0,
            Math.Max(0, arrangeSize.Width - targetWidth));
        DecreaseRepeatButton?.Arrange(new Rect(0, 0, left, arrangeSize.Height));
        Thumb.Arrange(new Rect(left, 0, targetWidth, arrangeSize.Height));
        IncreaseRepeatButton?.Arrange(new Rect(left + targetWidth, 0, Math.Max(0, arrangeSize.Width - left - targetWidth), arrangeSize.Height));
    }
}
