// WSL Container Desktop - a WinUI 3 manager for WSL containers.
// Copyright (C) 2026 Michael Hacker
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using Microsoft.UI.Xaml;

namespace WslContainerDesktop.Helpers;

/// <summary>
/// State trigger that activates when a target element's <see cref="FrameworkElement.ActualWidth"/>
/// falls within the half-open range [<see cref="MinWidth"/>, <see cref="MaxWidth"/>).
/// Lets a <c>VisualStateManager</c> switch layouts based on the actual content width of a page,
/// rather than the whole-window width observed by <c>AdaptiveTrigger</c>. Used to collapse wide
/// data tables into stacked "card" rows when the available width is narrow.
/// </summary>
public sealed class ControlSizeTrigger : StateTriggerBase
{
    /// <summary>Backing store for <see cref="MinWidth"/>; lets XAML set and bind the lower width bound.</summary>
    public static readonly DependencyProperty MinWidthProperty =
        DependencyProperty.Register(
            nameof(MinWidth),
            typeof(double),
            typeof(ControlSizeTrigger),
            new PropertyMetadata(0d, OnConditionChanged));

    /// <summary>Backing store for <see cref="MaxWidth"/>; lets XAML set and bind the upper width bound.</summary>
    public static readonly DependencyProperty MaxWidthProperty =
        DependencyProperty.Register(
            nameof(MaxWidth),
            typeof(double),
            typeof(ControlSizeTrigger),
            new PropertyMetadata(double.PositiveInfinity, OnConditionChanged));

    /// <summary>Backing store for <see cref="TargetElement"/>, the element whose width is watched.</summary>
    public static readonly DependencyProperty TargetElementProperty =
        DependencyProperty.Register(
            nameof(TargetElement),
            typeof(FrameworkElement),
            typeof(ControlSizeTrigger),
            new PropertyMetadata(null, OnTargetChanged));

    private FrameworkElement? _target;

    /// <summary>Inclusive lower bound of the width range that activates this trigger.</summary>
    public double MinWidth
    {
        get => (double)GetValue(MinWidthProperty);
        set => SetValue(MinWidthProperty, value);
    }

    /// <summary>Exclusive upper bound of the width range that activates this trigger.</summary>
    public double MaxWidth
    {
        get => (double)GetValue(MaxWidthProperty);
        set => SetValue(MaxWidthProperty, value);
    }

    /// <summary>The element whose width is observed. Typically the page root.</summary>
    public FrameworkElement? TargetElement
    {
        get => (FrameworkElement?)GetValue(TargetElementProperty);
        set => SetValue(TargetElementProperty, value);
    }

    private static void OnConditionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ControlSizeTrigger)d).Evaluate();

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var trigger = (ControlSizeTrigger)d;
        if (e.OldValue is FrameworkElement oldElement)
        {
            oldElement.SizeChanged -= trigger.OnTargetSizeChanged;
        }

        if (e.NewValue is FrameworkElement newElement)
        {
            trigger._target = newElement;
            newElement.SizeChanged += trigger.OnTargetSizeChanged;
        }
        else
        {
            trigger._target = null;
        }

        trigger.Evaluate();
    }

    private void OnTargetSizeChanged(object sender, SizeChangedEventArgs e) => Evaluate();

    private void Evaluate()
    {
        var width = _target?.ActualWidth ?? 0d;
        SetActive(width >= MinWidth && width < MaxWidth);
    }
}
