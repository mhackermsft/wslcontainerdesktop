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
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Dialogs;

/// <summary>
/// Authors a user template end-to-end in one dialog: metadata (name, category, description, glyph,
/// note) plus the payload — either the essential single-container run fields or a compose project
/// name + YAML. Used for New, Edit, and Duplicate. When editing/duplicating an existing template,
/// run-option fields the form doesn't surface (flags, labels, networks, …) are preserved so nothing
/// is silently dropped.
/// </summary>
public sealed class TemplateEditorDialog : ContentDialog
{
    private const string DefaultGlyph = "\uE7B8"; // generic package

    private readonly string _id;
    private readonly TemplateSource _source;
    private readonly RunContainerOptions _baseOptions;

    private readonly TextBox _nameBox;
    private readonly ComboBox _categoryBox;
    private readonly TextBox _descriptionBox;
    private readonly GridView _glyphGrid;
    private string _selectedGlyph;
    private readonly TextBox _noteBox;
    private readonly RadioButton _containerKind;
    private readonly RadioButton _composeKind;
    private readonly StackPanel _containerPanel;
    private readonly StackPanel _composePanel;

    private readonly TextBox _imageBox;
    private readonly TextBox _containerNameBox;
    private readonly TextBox _portsBox;
    private readonly TextBox _envBox;
    private readonly TextBox _volumesBox;

    private readonly TextBox _projectNameBox;
    private readonly TextBox _yamlBox;

    /// <summary>
    /// Creates the editor. Pass <paramref name="source"/> null for a brand-new template; otherwise the
    /// fields are prefilled. <paramref name="isDuplicate"/> makes a copy (fresh id, "(copy)" name),
    /// otherwise the same id is kept (edit in place). <paramref name="categories"/> seeds the category
    /// suggestions.
    /// </summary>
    public TemplateEditorDialog(
        IEnumerable<string> categories,
        StackTemplate? source = null,
        bool isDuplicate = false)
    {
        var editing = source is not null && !isDuplicate;
        _id = editing ? source!.Id : $"user-{Guid.NewGuid():N}"[..13];
        _source = TemplateSource.User;
        _baseOptions = source?.RunOptions?.Clone() ?? new RunContainerOptions();

        Title = source is null
            ? "New template"
            : (isDuplicate ? $"Duplicate {source.Name}" : $"Edit {source.Name}");
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        Resources["ContentDialogMaxWidth"] = 780.0;
        Resources["ContentDialogMinWidth"] = 660.0;

        var defaultName = source is null
            ? string.Empty
            : (isDuplicate ? $"{source.Name} (copy)" : source.Name);

        _nameBox = new TextBox { Header = "Name", Text = defaultName, PlaceholderText = "e.g. My PostgreSQL" };

        _categoryBox = new ComboBox
        {
            Header = "Category",
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "e.g. Databases",
        };
        foreach (var category in categories.Distinct().OrderBy(c => c))
        {
            _categoryBox.Items.Add(category);
        }

        _descriptionBox = new TextBox
        {
            Header = "Description",
            Text = source?.Description ?? string.Empty,
            PlaceholderText = "Short one-line summary shown on the card",
        };

        _selectedGlyph = NormalizeGlyph(source?.Glyph);
        _glyphGrid = BuildGlyphPicker(_selectedGlyph);
        _glyphGrid.SelectionChanged += (_, _) =>
        {
            if (_glyphGrid.SelectedItem is FontIcon { Tag: string g })
            {
                _selectedGlyph = g;
            }
        };

        _noteBox = new TextBox
        {
            Header = "Note (optional)",
            Text = source?.Note ?? string.Empty,
            PlaceholderText = "e.g. default credentials, shown after launch",
        };

        _containerKind = new RadioButton { Content = "Single container", GroupName = "kind" };
        _composeKind = new RadioButton { Content = "Compose stack", GroupName = "kind" };
        var isCompose = source?.Kind == StackTemplateKind.Compose;
        _containerKind.IsChecked = !isCompose;
        _composeKind.IsChecked = isCompose;
        _containerKind.Checked += (_, _) => UpdatePanels();
        _composeKind.Checked += (_, _) => UpdatePanels();

        // Container payload fields.
        _imageBox = new TextBox
        {
            Header = "Image",
            Text = _baseOptions.Image,
            PlaceholderText = "e.g. postgres:16",
        };
        _containerNameBox = new TextBox
        {
            Header = "Container name (optional)",
            Text = _baseOptions.Name ?? string.Empty,
            PlaceholderText = "e.g. my-postgres",
        };
        _portsBox = MakeMultiline("Ports (one per line, host:container)", _baseOptions.PortMappings, "5432:5432");
        _envBox = MakeMultiline("Environment (one per line, KEY=VALUE)", _baseOptions.EnvironmentVariables, "POSTGRES_PASSWORD=secret");
        _volumesBox = MakeMultiline("Volumes (one per line, source:destination)", _baseOptions.Volumes, "pgdata:/var/lib/postgresql/data");

        _containerPanel = new StackPanel
        {
            Spacing = 10,
            Children = { _imageBox, _containerNameBox, _portsBox, _envBox, _volumesBox },
        };

        // Compose payload fields.
        _projectNameBox = new TextBox
        {
            Header = "Project name (optional)",
            Text = source?.ComposeProjectName ?? string.Empty,
            PlaceholderText = "e.g. my-stack",
        };
        _yamlBox = new TextBox
        {
            Header = "Compose YAML",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Height = 260,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _yamlBox.Text = NormalizeToCrLf(source?.ComposeYaml ?? string.Empty);
        ScrollViewer.SetHorizontalScrollBarVisibility(_yamlBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_yamlBox, ScrollBarVisibility.Auto);
        _composePanel = new StackPanel
        {
            Spacing = 10,
            Children = { _projectNameBox, _yamlBox },
        };

        // Set the editable category text AFTER items are added so it isn't cleared.
        _categoryBox.Text = source?.Category ?? string.Empty;

        var kindRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Children = { _containerKind, _composeKind },
        };

        var body = new StackPanel
        {
            Spacing = 12,
            MinWidth = 620,
            Children =
            {
                _nameBox,
                _categoryBox,
                _descriptionBox,
                _glyphGrid,
                _noteBox,
                new TextBlock { Text = "Type", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                kindRow,
                _containerPanel,
                _composePanel,
            },
        };

        Content = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 560,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        UpdatePanels();
        PrimaryButtonClick += OnPrimary;
    }

    /// <summary>The assembled template, valid only after the dialog returns Primary.</summary>
    public StackTemplate? Result { get; private set; }

    private static TextBox MakeMultiline(string header, IEnumerable<string> lines, string placeholder)
    {
        var box = new TextBox
        {
            Header = header,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            MinHeight = 64,
            PlaceholderText = placeholder,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        box.Text = string.Join("\r\n", lines);
        return box;
    }

    private void UpdatePanels()
    {
        var compose = _composeKind.IsChecked == true;
        _composePanel.Visibility = compose ? Visibility.Visible : Visibility.Collapsed;
        _containerPanel.Visibility = compose ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var name = _nameBox.Text?.Trim() ?? string.Empty;
        var category = (_categoryBox.Text ?? string.Empty).Trim();
        var compose = _composeKind.IsChecked == true;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(category))
        {
            args.Cancel = true;
            return;
        }

        if (compose)
        {
            var yaml = NormalizeToLf(_yamlBox.Text ?? string.Empty);
            if (string.IsNullOrWhiteSpace(yaml))
            {
                args.Cancel = true;
                return;
            }

            Result = new StackTemplate
            {
                Id = _id,
                Name = name,
                Category = category,
                Description = _descriptionBox.Text?.Trim() ?? string.Empty,
                Glyph = _selectedGlyph,
                Note = EmptyToNull(_noteBox.Text),
                Kind = StackTemplateKind.Compose,
                ComposeYaml = yaml,
                ComposeProjectName = EmptyToNull(_projectNameBox.Text),
                Source = _source,
            };
            return;
        }

        var image = _imageBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(image))
        {
            args.Cancel = true;
            return;
        }

        // Preserve unexposed run-option fields by mutating the cloned base.
        _baseOptions.Image = image;
        _baseOptions.Name = EmptyToNull(_containerNameBox.Text);
        _baseOptions.PortMappings = SplitLines(_portsBox.Text);
        _baseOptions.EnvironmentVariables = SplitLines(_envBox.Text);
        _baseOptions.Volumes = SplitLines(_volumesBox.Text);

        Result = new StackTemplate
        {
            Id = _id,
            Name = name,
            Category = category,
            Description = _descriptionBox.Text?.Trim() ?? string.Empty,
            Glyph = _selectedGlyph,
            Note = EmptyToNull(_noteBox.Text),
            Kind = StackTemplateKind.SingleContainer,
            RunOptions = _baseOptions,
            Source = _source,
        };
    }

    private static List<string> SplitLines(string? text) =>
        (text ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

    private static string? EmptyToNull(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string NormalizeGlyph(string? text)
    {
        var glyph = (text ?? string.Empty).Trim();
        return glyph.Length == 0 ? DefaultGlyph : glyph;
    }

    /// <summary>
    /// A curated palette of Segoe Fluent / MDL2 glyphs so authors pick an icon visually instead of
    /// hunting Unicode codepoints. The template's current glyph is always included (and preselected)
    /// even if it isn't in the palette, so editing never loses a hand-picked icon.
    /// </summary>
    private static readonly string[] GlyphChoices =
    {
        "\uE7B8", "\uE8B7", "\uE8F1", "\uE7C3", "\uE8A5", "\uE753", "\uE774", "\uE80F",
        "\uE945", "\uE734", "\uE72E", "\uE90F", "\uE713", "\uE71D", "\uE896", "\uE898",
        "\uE895", "\uE946", "\uE768", "\uE8C8", "\uE710", "\uE839", "\uE704", "\uE968",
        "\uE9D9", "\uEB05", "\uE72C", "\uE787", "\uE716", "\uE77B", "\uE115", "\uE7C1",
    };

    private static GridView BuildGlyphPicker(string selectedGlyph)
    {
        var grid = new GridView
        {
            Header = "Icon glyph",
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = false,
            Height = 148,
            Padding = new Thickness(2),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Disabled);

        var codes = new List<string>(GlyphChoices);
        if (!codes.Contains(selectedGlyph))
        {
            codes.Insert(0, selectedGlyph);
        }

        FontIcon? preselect = null;
        foreach (var code in codes)
        {
            var icon = new FontIcon
            {
                Glyph = code,
                FontSize = 22,
                Width = 40,
                Height = 40,
                Tag = code,
            };
            grid.Items.Add(icon);
            if (code == selectedGlyph)
            {
                preselect = icon;
            }
        }

        grid.SelectedItem = preselect;
        return grid;
    }

    private static string NormalizeToCrLf(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    private static string NormalizeToLf(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');
}
