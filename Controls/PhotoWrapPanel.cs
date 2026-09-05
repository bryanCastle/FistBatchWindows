using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace PhotoOrganizer.Controls;

internal sealed class PhotoWrapPanel : Panel
{
    public double HorizontalSpacing { get; set; }
    public double VerticalSpacing { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var availableWidth = double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width;
        var x = 0d;
        var y = 0d;
        var rowHeight = 0d;

        foreach (var child in Children)
        {
            child.Measure(new Size(availableWidth, availableSize.Height));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableWidth)
            {
                x = 0;
                y += rowHeight + VerticalSpacing;
                rowHeight = 0;
            }

            x += size.Width + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        return new Size(availableWidth, y + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;
        var y = 0d;
        var rowHeight = 0d;

        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                x = 0;
                y += rowHeight + VerticalSpacing;
                rowHeight = 0;
            }

            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        return finalSize;
    }
}
