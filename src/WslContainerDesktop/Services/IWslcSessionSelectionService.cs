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

namespace WslContainerDesktop.Services;

/// <summary>Chooses and validates a session for a user-initiated operation.</summary>
public interface IWslcSessionSelectionService
{
    Task<WslcSessionSelection?> ChooseAsync(CancellationToken ct = default);
    Task<WslcSessionSelection?> ChooseForLaterAsync(CancellationToken ct = default);
    Task<WslcSessionSelection?> ChooseActiveAsync(CancellationToken ct = default);
    Task VerifyAsync(WslcSessionSelection selection, CancellationToken ct = default);
    Task VerifyForLaterAsync(WslcSessionSelection selection, CancellationToken ct = default);
    IWslcService CreateClient(WslcSessionSelection selection);
}
