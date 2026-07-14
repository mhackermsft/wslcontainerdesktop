using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WslContainerDesktop.Helpers;

/// <summary>
/// Minimal wrapping panel: arranges children left-to-right and moves to the next line when the
/// current line would overflow the available width. Used so dashboard stat tiles reflow onto
/// additional rows in narrow windows instead of being clipped or scrolled.
/// </summary>
public sealed class WrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(
            nameof(HorizontalSpacing),
            typeof(double),
            typeof(WrapPanel),
            new PropertyMetadata(0d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(
            nameof(VerticalSpacing),
            typeof(double),
            typeof(WrapPanel),
            new PropertyMetadata(0d, OnLayoutPropertyChanged));

    /// <summary>Gap between items on the same line.</summary>
    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    /// <summary>Gap between wrapped lines.</summary>
    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((WrapPanel)d).InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        var lineWidth = 0d;
        var lineHeight = 0d;
        var totalWidth = 0d;
        var totalHeight = 0d;
        var maxWidth = availableSize.Width;

        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var desired = child.DesiredSize;

            var addWidth = lineWidth > 0 ? HorizontalSpacing + desired.Width : desired.Width;
            if (lineWidth > 0 && lineWidth + addWidth > maxWidth)
            {
                // Wrap to a new line.
                totalWidth = Math.Max(totalWidth, lineWidth);
                totalHeight += lineHeight + VerticalSpacing;
                lineWidth = desired.Width;
                lineHeight = desired.Height;
            }
            else
            {
                lineWidth += addWidth;
                lineHeight = Math.Max(lineHeight, desired.Height);
            }
        }

        totalWidth = Math.Max(totalWidth, lineWidth);
        totalHeight += lineHeight;

        if (double.IsInfinity(maxWidth))
        {
            return new Size(totalWidth, totalHeight);
        }

        return new Size(Math.Min(totalWidth, maxWidth), totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;
        var y = 0d;
        var lineHeight = 0d;
        var maxWidth = finalSize.Width;

        foreach (var child in Children)
        {
            var desired = child.DesiredSize;
            var addWidth = x > 0 ? HorizontalSpacing + desired.Width : desired.Width;

            if (x > 0 && x + addWidth > maxWidth)
            {
                x = 0;
                y += lineHeight + VerticalSpacing;
                lineHeight = 0;
                addWidth = desired.Width;
            }

            var left = x > 0 ? x + HorizontalSpacing : x;
            child.Arrange(new Rect(left, y, desired.Width, desired.Height));
            x = left + desired.Width;
            lineHeight = Math.Max(lineHeight, desired.Height);
        }

        return finalSize;
    }
}
