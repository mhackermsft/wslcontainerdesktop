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

using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Works out which images a container holds, so space that cannot actually be freed is not offered
/// as reclaimable.
///
/// The engine refuses to remove an image a container references, including a stopped one. A pull
/// that moves a tag leaves the previous image untagged, so an image can be both dangling and in use
/// at the same time — which is exactly the case where offering to "reclaim" it produces a prune that
/// frees nothing and a row that stubbornly stays put.
/// </summary>
public static class ImageUsageResolver
{
    /// <summary>
    /// Stamps <see cref="ImageInfo.UsedBy"/> on every image a container references, and clears it on
    /// the rest. Containers report the image as an ID (short or full) or as a reference, so both are
    /// matched. Ambiguity resolves toward "in use", because wrongly offering to reclaim occupied
    /// space is the failure worth avoiding; wrongly withholding it only under-reports.
    /// </summary>
    public static void Apply(IReadOnlyList<ImageInfo> images, IReadOnlyList<ContainerInfo> containers)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(containers);

        foreach (var image in images)
        {
            var holders = containers
                .Where(c => Uses(c, image))
                .Select(c => string.IsNullOrWhiteSpace(c.Name) ? c.Id : c.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            image.UsedBy = string.Join(", ", holders);
        }
    }

    private static bool Uses(ContainerInfo container, ImageInfo image)
    {
        var used = (container.Image ?? string.Empty).Trim();
        if (used.Length == 0)
            return false;

        // A reference the user would recognize: "mysql:8", or a bare repository for a sole tag.
        if (string.Equals(used, image.Reference, StringComparison.Ordinal) ||
            string.Equals(used, image.Repository, StringComparison.Ordinal))
        {
            return true;
        }

        // Otherwise an ID, which the two commands report at different widths.
        return ContainerIdentity.ResolveId([Normalize(image.Id)], Normalize(used)) is not null;
    }

    private static string Normalize(string id) =>
        id.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? id[7..] : id;
}
