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

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WslContainerDesktop.Helpers;
using Xunit;

namespace WslContainerDesktop.Tests.Helpers;

public sealed class ResourceGridViewTests
{
    [Fact]
    public void FilterPreservesSessionQualifiedRowsAndAuthoritativeInventory()
    {
        var first = new Row("alpha", "redis", 80);
        var second = new Row("beta", "redis", 443);
        var source = new ObservableCollection<Row> { first, second };
        var grid = Create(source);

        grid.SearchText = "BETA";
        Assert.Same(second, Assert.Single(grid.Items));
        Assert.Equal(new[] { first, second }, source);
        Assert.False(grid.HasNoMatches);

        grid.SearchText = "not found";
        Assert.Empty(grid.Items);
        Assert.True(grid.HasNoMatches);
        grid.SearchText = string.Empty;
        Assert.Equal(new[] { first, second }, grid.Items);
        Assert.False(grid.HasNoMatches);
    }

    [Fact]
    public void FilterIndicatorTracksEffectiveSearchAndPreservesItsText()
    {
        var grid = Create(new ObservableCollection<Row> { new("alpha", "redis", 80) });
        var changed = new List<string?>();
        grid.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.False(grid.HasFilter);
        grid.SearchText = "  redis  ";
        Assert.True(grid.HasFilter);
        Assert.Equal("  redis  ", grid.SearchText);
        Assert.Contains("redis", grid.FilterButtonName);
        Assert.Contains(nameof(grid.HasFilter), changed);
        Assert.Contains(nameof(grid.FilterButtonCaption), changed);
        Assert.Contains(nameof(grid.FilterButtonName), changed);

        grid.RefreshLocalizedText();
        Assert.True(grid.HasFilter);
        Assert.Equal("  redis  ", grid.SearchText);
        grid.SearchText = "   ";
        Assert.False(grid.HasFilter);
        Assert.False(grid.HasNoMatches);
        Assert.Single(grid.Items);
        grid.SearchText = string.Empty;
        Assert.False(grid.HasFilter);
    }

    [Fact]
    public void SortUsesTypedValuesAndDoesNotMutateSource()
    {
        var first = new Row("a", "first", 80);
        var second = new Row("b", "second", 9);
        var third = new Row("c", "third", null);
        var source = new ObservableCollection<Row> { first, second, third };
        var grid = Create(source);

        grid.SortBy("port");
        Assert.Equal(new[] { second, first, third }, grid.Items);
        grid.SortBy("port");
        Assert.Equal(new[] { first, second, third }, grid.Items);
        Assert.Equal(new[] { first, second, third }, source);
        Assert.True(grid.Descending);
    }

    [Fact]
    public void RowUpdatesAndInventoryChangesReapplyFilterAndSort()
    {
        var first = new Row("a", "redis", 80);
        var second = new Row("b", "other", 9);
        var source = new ObservableCollection<Row> { first, second };
        var grid = Create(source);
        grid.SearchText = "redis";
        grid.SortBy("port");

        second.Name = "redis";
        Assert.Equal(new[] { second, first }, grid.Items);
        source.Remove(second);
        Assert.Same(first, Assert.Single(grid.Items));
        second.Name = "ignored";
        Assert.Same(first, Assert.Single(grid.Items));
        source.Clear();
        Assert.False(grid.HasNoMatches);
    }

    [Fact]
    public void RefreshingLocalizedLabelsKeepsFilterAndSortState()
    {
        var grid = Create(new ObservableCollection<Row> { new("a", "redis", 80) });
        grid.SearchText = "redis";
        grid.SortBy("port");
        grid.Descending = true;
        var option = grid.SelectedSort;

        grid.RefreshLocalizedText();

        Assert.Same(option, grid.SelectedSort);
        Assert.Equal("redis", grid.SearchText);
        Assert.True(grid.Descending);
        Assert.Single(grid.Items);
    }

    [Fact]
    public void DeferralReprojectsOnceAfterSourceAndRowChanges()
    {
        var first = new Row("a", "redis", 80);
        var source = new ObservableCollection<Row> { first };
        var grid = Create(source);
        grid.SearchText = "redis";
        var updates = 0;
        grid.ProjectionUpdated += (_, _) => updates++;

        using (grid.DeferRefresh())
        {
            first.Name = "changed";
            source.Add(new Row("b", "redis", 9));
            using (grid.DeferRefresh())
                source.Add(new Row("c", "redis", 7));
            Assert.Equal(0, updates);
        }

        Assert.Equal(1, updates);
        Assert.Equal(["b", "c"], grid.Items.Select(row => row.Session));
        grid.Items[0].Name = "changed";
        Assert.Equal("c", Assert.Single(grid.Items).Session);
    }

    private static ResourceGridView<Row> Create(ObservableCollection<Row> source) =>
        new(source, row => [row.Session, row.Name, row.Port?.ToString()],
            new("name", "ResourceGrid_Name", "Name", row => row.Name),
            new("port", "ResourceGrid_HostPort", "Host port", row => row.Port));

    private sealed class Row(string session, string name, int? port) : ObservableObject
    {
        private string _name = name;
        public string Session { get; } = session;
        public string Name { get => _name; set => SetProperty(ref _name, value); }
        public int? Port { get; } = port;
    }
}
