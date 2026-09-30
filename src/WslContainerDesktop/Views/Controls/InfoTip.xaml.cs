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
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WslContainerDesktop.Helpers;

namespace WslContainerDesktop.Views.Controls;

/// <summary>Reusable help button that shows short plain-language explanations for advanced CLI options.</summary>
public sealed partial class InfoTip : UserControl
{
    /// <summary>Dependency property backing <see cref="Title"/> for XAML binding.</summary>
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(InfoTip), new PropertyMetadata(string.Empty, OnContentChanged));

    /// <summary>Dependency property backing <see cref="Text"/> for XAML binding.</summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(InfoTip), new PropertyMetadata(string.Empty, OnContentChanged));

    /// <summary>Dependency property backing <see cref="Flag"/> for XAML binding.</summary>
    public static readonly DependencyProperty FlagProperty =
        DependencyProperty.Register(nameof(Flag), typeof(string), typeof(InfoTip), new PropertyMetadata(string.Empty, OnContentChanged));

    /// <summary>Initializes the page/control and resolves its view model from the app service provider.</summary>
    public InfoTip()
    {
        InitializeComponent();
        UpdateContent();
    }

    /// <summary>Short heading shown in the flyout and accessibility label.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Explanation shown in the tooltip and flyout.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Optional CLI flag or setting name shown in monospace text.</summary>
    public string Flag
    {
        get => (string)GetValue(FlagProperty);
        set => SetValue(FlagProperty, value);
    }

    /// <summary>Creates an <see cref="InfoTip"/> from explicit text values.</summary>
    public static InfoTip Create(string title, string text, string? flag = null) =>
        new()
        {
            Title = title,
            Text = text,
            Flag = flag ?? string.Empty,
        };

    /// <summary>Creates an <see cref="InfoTip"/> from a shared <see cref="FlagHelpEntry"/>.</summary>
    public static InfoTip Create(FlagHelpEntry help) => Create(help.Title, help.Text, help.Flag);

    /// <summary>Returns a horizontal panel that places a control beside its help tip.</summary>
    public static StackPanel Labeled(UIElement control, InfoTip tip) =>
        new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { control, tip },
        };

    /// <summary>Returns a header label with an adjacent help tip built from explicit text.</summary>
    public static StackPanel Header(string headerText, string title, string text, string? flag = null) =>
        new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = headerText, VerticalAlignment = VerticalAlignment.Center },
                Create(title, text, flag),
            },
        };

    /// <summary>Returns a header label with an adjacent help tip built from shared flag help.</summary>
    public static StackPanel Header(string headerText, FlagHelpEntry help) =>
        Header(headerText, help.Title, help.Text, help.Flag);

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is InfoTip tip)
        {
            tip.UpdateContent();
        }
    }

    private void UpdateContent()
    {
        var title = string.IsNullOrWhiteSpace(Title) ? null : Title.Trim();
        var flag = string.IsNullOrWhiteSpace(Flag) ? null : Flag.Trim();
        var text = Text ?? string.Empty;
        var about = title ?? flag ?? "this option";

        AutomationProperties.SetName(InfoButton, $"More information about {about}");
        AutomationProperties.SetHelpText(InfoButton, text);
        ToolTipService.SetToolTip(InfoButton, new ToolTip
        {
            Content = new TextBlock
            {
                Text = text,
                MaxWidth = 360,
                TextWrapping = TextWrapping.Wrap,
            },
        });
    }

    private void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel
        {
            Spacing = 8,
            MaxWidth = 380,
        };

        if (!string.IsNullOrWhiteSpace(Title))
        {
            panel.Children.Add(new TextBlock
            {
                Text = Title,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (!string.IsNullOrWhiteSpace(Flag))
        {
            panel.Children.Add(new TextBlock
            {
                Text = Flag,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = Text,
            IsTextSelectionEnabled = true,
            MaxWidth = 380,
            TextWrapping = TextWrapping.Wrap,
        });

        var flyout = new Flyout
        {
            Content = panel,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,
        };
        flyout.ShowAt(InfoButton);
    }
}
