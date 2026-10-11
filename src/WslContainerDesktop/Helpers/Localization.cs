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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.Helpers;

/// <summary>Reapplies translated resource properties in place, preserving the loaded view's state.</summary>
public static class Localization
{
    private static readonly List<WeakReference<DependencyObject>> Targets = new();
    static Localization() => UiText.LanguageChanged += (_, _) => RefreshAll();

    public static readonly DependencyProperty UidProperty = DependencyProperty.RegisterAttached(
        "Uid", typeof(string), typeof(Localization), new PropertyMetadata(null, OnUidChanged));
    public static string GetUid(DependencyObject element) => (string)element.GetValue(UidProperty);
    public static void SetUid(DependencyObject element, string value) => element.SetValue(UidProperty, value);

    private static void OnUidChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        Targets.Add(new(element));
        Apply(element);
    }

    private static void RefreshAll()
    {
        for (var index = Targets.Count - 1; index >= 0; index--)
        {
            if (Targets[index].TryGetTarget(out var element)) Apply(element);
            else Targets.RemoveAt(index);
        }
    }

    private static void Apply(DependencyObject element)
    {
        var uid = GetUid(element);
        if (string.IsNullOrEmpty(uid)) return;
        foreach (var (property, value) in UiText.Properties(uid))
        {
            if (property.EndsWith("AutomationProperties.Name", StringComparison.Ordinal))
                AutomationProperties.SetName(element, value);
            else if (property.EndsWith("ToolTipService.ToolTip", StringComparison.Ordinal))
                ToolTipService.SetToolTip(element, value);
            else
            {
                var setter = element.GetType().GetProperty(property);
                if (setter is { CanWrite: true } && (setter.PropertyType == typeof(string) || setter.PropertyType == typeof(object)))
                    setter.SetValue(element, value);
                else
                    throw new InvalidOperationException($"Resource '{uid}.{property}' cannot be applied to {element.GetType().Name}.");
            }
        }
    }
}
