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

namespace WslContainerDesktop.Models;

/// <summary>Values that describe wslc feature states or choices in WSL Container Desktop workflows.</summary>
public enum WslcFeature
{
    /// <summary><c>wslc run --health-start-interval</c>; not advertised by the 3.0.1 baseline.</summary>
    HealthStartInterval,

    /// <summary><c>wslc create --health-start-interval</c>; not advertised by the 3.0.1 baseline.</summary>
    CreateHealthStartInterval,
}
