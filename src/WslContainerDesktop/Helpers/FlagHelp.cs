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

using WslContainerDesktop.Services;

namespace WslContainerDesktop.Helpers;

/// <summary>Short help text for a command-line flag or concept shown beside an option in the UI.</summary>
/// <param name="Key">Stable key used to identify the help entry.</param>
/// <param name="Title">Human-readable title shown in the tip.</param>
/// <param name="Flag">The <c>wslc</c> flag, command, or setting name being explained.</param>
/// <param name="Text">Plain-language explanation aimed at users who do not know the CLI.</param>
public sealed record FlagHelpEntry(string Key, string Title, string Flag, string Text);

/// <summary>Catalog of short explanations used by <c>InfoTip</c> controls throughout the app.</summary>
public static class FlagHelp
{
    /// <summary>Help entry for pulling every tag from an image repository.</summary>
    public static FlagHelpEntry PullAllTags => Entry(
        "pull-all-tags",
        UiText.Get("Common_Text0273", "All tags"),
        "--all-tags",
        UiText.Get("Common_Text0274", "wslc pull --all-tags downloads every tagged image in the repository. wslc cannot combine a specific tag with --all-tags, so this app drops the tag you typed before pulling. Public repositories can have hundreds of tags and use a lot of disk space."));

    /// <summary>Help entry for pushing every local tag for an image repository.</summary>
    public static FlagHelpEntry PushAllTags => Entry(
        "push-all-tags",
        UiText.Get("Common_Text0273", "All tags"),
        "--all-tags",
        UiText.Get("Common_Text0275", "wslc push --all-tags pushes every local tag for the selected repository. wslc cannot combine a specific tag with --all-tags, so this app drops the tag you typed before pushing."));

    /// <summary>Help entry for running a container in the background.</summary>
    public static FlagHelpEntry RunDetach => Entry(
        "run-detach",
        UiText.Get("Common_Text0276", "Run in background"),
        "-d, --detach",
        UiText.Get("Common_Text0278", "wslc run -d starts the container and returns immediately, leaving it running in the background. With it off, the run waits until the container's main process exits. Leave it on for services such as databases and web servers."));

    /// <summary>Help entry for removing a container automatically when it exits.</summary>
    public static FlagHelpEntry RunRemove => Entry(
        "run-remove",
        UiText.Get("Common_Text0279", "Remove when it exits"),
        "--rm",
        UiText.Get("Common_Text0280", "wslc run --rm deletes the container after its main process stops. Use it for disposable runs; writable-layer changes in the container are lost unless they were written to a volume or bind mount."));

    /// <summary>Help entry for keeping standard input open for a container.</summary>
    public static FlagHelpEntry RunInteractive => Entry(
        "run-interactive",
        UiText.Get("Common_Text0281", "Keep STDIN open"),
        "-i, --interactive",
        UiText.Get("Common_Text0283", "wslc run -i keeps standard input open for the container process. In this app it is only allowed with -d, because a foreground interactive run would wait for terminal input the dialog cannot provide."));

    /// <summary>Help entry for passing GPU devices through to a container.</summary>
    public static FlagHelpEntry RunGpus => Entry(
        "run-gpus",
        UiText.Get("Common_Text0284", "Pass all GPUs"),
        "--gpus all",
        UiText.Get("Common_Text0286", "wslc run --gpus all requests all available GPU devices for the container. It means the engine will pass devices through when supported; it does not prove the image or workload is using acceleration."));

    /// <summary>Help entry for choosing a container network.</summary>
    public static FlagHelpEntry RunNetwork => Entry(
        "run-network",
        UiText.Get("Common_Text0239", "Network"),
        "--network",
        UiText.Get("Common_Text0287", "wslc run --network connects the container to the named network instead of the engine's default bridge network. Custom IP addresses and aliases only apply to a user-created network."));

    /// <summary>Help entry for configuring the container stop timeout.</summary>
    public static FlagHelpEntry RunStopTimeout => Entry(
        "run-stop-timeout",
        UiText.Get("Common_Text0288", "Stop timeout"),
        "--stop-timeout",
        UiText.Get("Common_Text0289", "wslc run --stop-timeout sets how many seconds a stop waits for the container to exit after the stop signal before killing it. -1 waits indefinitely. Leave it blank to use the engine's default."));

    /// <summary>Help entry for creating an internal-only network.</summary>
    public static FlagHelpEntry NetworkInternal => Entry(
        "network-internal",
        UiText.Get("Common_Text0290", "Internal network"),
        "--internal",
        UiText.Get("Common_Text0291", "wslc network create --internal restricts external and outbound access for the network. Containers attached to the network can still communicate with each other."));

    /// <summary>Help entry for network driver-specific options.</summary>
    public static FlagHelpEntry NetworkDriverOptions => Entry(
        "network-driver-options",
        UiText.Get("Common_Text0292", "Driver options"),
        "-o, --opt",
        UiText.Get("Common_Text0294", "wslc network create --opt passes driver-specific key=value options to the network driver. Only use options supported by the selected driver."));

    /// <summary>Help entry for adding DNS aliases on a container network.</summary>
    public static FlagHelpEntry NetworkAliases => Entry(
        "network-aliases",
        UiText.Get("Common_Extra0518", "Aliases"),
        "--network-alias",
        UiText.Get("Common_Text0295", "wslc network connect --network-alias adds extra DNS names for this container on that network. Other containers on the same network can use those names to reach it."));

    /// <summary>Help entry for saving an image archive.</summary>
    public static FlagHelpEntry ImageSave => Entry(
        "image-save",
        UiText.Get("Common_Extra0519", "Save"),
        "-o, --output",
        UiText.Get("Common_Text0297", "Writes the image to a .tar file, with its names, tags, layers and start command, for example to copy it to another PC or keep as a backup. To bring it back, use Import → Restore saved images… on the Images page."));

    /// <summary>Help entry for writable and virtual container sizes.</summary>
    public static FlagHelpEntry ContainersSize => Entry(
        "containers-size",
        UiText.Get("Common_Extra0520", "Size"),
        "-s, --size",
        UiText.Get("Common_Text0299", "wslc list --size reports two sizes. The writable size is what the container has written itself; the virtual size also counts the read-only image layers. Image layers can be shared by several containers, so virtual sizes do not add up to disk use. Hover a size in the list to see both."));

    /// <summary>Help entry for showing stopped containers in the list.</summary>
    public static FlagHelpEntry ContainersShowAll => Entry(
        "containers-show-all",
        UiText.Get("Common_Text0300", "Show all"),
        "-a, --all",
        UiText.Get("Common_Text0302", "wslc list --all shows stopped containers as well as running ones. With it off, the engine lists only running containers."));

    /// <summary>Help entry for attaching a terminal to a container process.</summary>
    public static FlagHelpEntry ContainerAttach => Entry(
        "container-attach",
        UiText.Get("Common_Extra0521", "Attach"),
        "attach",
        UiText.Get("Common_Text0303", "wslc attach connects your terminal to the container's main process. Input such as Ctrl+C goes to that process and may stop the container."));

    /// <summary>Help entry for exporting a container filesystem.</summary>
    public static FlagHelpEntry ContainerExport => Entry(
        "container-export",
        UiText.Get("Common_Text0304", "Export filesystem"),
        "wslc export",
        UiText.Get("Common_Text0306", "Writes a copy of all the container's files to a .tar file. Mounted volumes, the start command, environment variables and ports are not included. To turn the file into an image, use Import → Create image from exported files… on the Images page."));

    /// <summary>Help entry for restarting a container.</summary>
    public static FlagHelpEntry ContainerRestart => Entry(
        "container-restart",
        UiText.Get("Common_Extra0522", "Restart"),
        "restart",
        UiText.Get("Common_Text0307", "wslc restart stops and starts a container, and starts it if it is currently stopped. It uses the container's configured stop signal and timeout unless options override them."));

    /// <summary>Help entry for force-killing a container.</summary>
    public static FlagHelpEntry ContainerKill => Entry(
        "container-kill",
        UiText.Get("Common_Extra0523", "Kill"),
        "kill",
        UiText.Get("Common_Text0308", "wslc kill sends SIGKILL by default to force a running container to stop. Use it when Stop does not finish cleanly; the process cannot shut down gracefully."));

    /// <summary>Help entry for installing early-access WSL builds.</summary>
    public static FlagHelpEntry WslPreRelease => Entry(
        "wsl-pre-release",
        UiText.Get("Common_Text0309", "Include early-access builds"),
        "--update --pre-release",
        UiText.Get("Common_Text0311", "wsl --update --pre-release checks the early-access WSL update channel. These builds can contain newer fixes but may be less stable than the normal channel."));

    /// <summary>Help entry for changing WSL container session storage.</summary>
    public static FlagHelpEntry StorageChangeLocation => Entry(
        "storage-change-location",
        UiText.Get("Common_Text0312", "Change location"),
        "session.storagePath",
        UiText.Get("Common_Text0313", "session.storagePath changes where future WSL container session storage is created. The folder must be empty, existing data is not moved, and the WSL container session must restart before the change is used."));

    /// <summary>Help entry for terminating and recreating the WSL container session.</summary>
    public static FlagHelpEntry RestartWslSession => Entry(
        "restart-wsl-session",
        UiText.Get("Common_Text0314", "Restart WSL session"),
        "wslc system session terminate",
        UiText.Get("Common_Text0316", "wslc system session terminate ends the WSL container session. Every running container in it stops; the session starts again automatically on the next container command, and containers must be started again."));

    /// <summary>Help entry for shutting down all WSL distributions.</summary>
    public static FlagHelpEntry ShutdownWsl => Entry(
        "shutdown-wsl",
        UiText.Get("Common_Text0317", "Shut down WSL"),
        "--shutdown",
        UiText.Get("Common_Text0318", "wsl --shutdown stops all WSL distributions and the container session. Running containers stop with it and WSL starts again on the next use."));

    /// <summary>Help entry for rebuilding Compose images before applying a project.</summary>
    public static FlagHelpEntry ComposeRebuild => Entry(
        "compose-rebuild",
        UiText.Get("Common_Text0319", "Rebuild images when applying"),
        "build",
        UiText.Get("Common_Text0320", "Rebuilds service images before applying the selected Compose services. Use it after Dockerfile or build-context changes; it can take longer than reusing existing images."));

    /// <summary>Help entry for how long k3s keeps running inside its WSL distribution.</summary>
    public static FlagHelpEntry KubernetesLifetime => Entry(
        "kubernetes-lifetime",
        UiText.Get("Common_Text0321", "When k3s runs"),
        "instanceIdleTimeout",
        UiText.Get("Common_Text0322", "k3s runs only while WSL keeps its distribution running. While this app is open it keeps that distribution running, unless you click Stop. After you quit the app, WSL stops the distribution once it's idle (15 seconds by default) and k3s and its pods stop with it, just as for a k3s you install yourself. To keep k3s running without the app, set instanceIdleTimeout=-1 under [general] in %UserProfile%\\.wslconfig. That setting applies to every distribution."));

    /// <summary>All help entries, used when a page wants to search or enumerate the help catalog.</summary>
    public static IReadOnlyList<FlagHelpEntry> All =>
    [
        PullAllTags,
        PushAllTags,
        RunDetach,
        RunRemove,
        RunInteractive,
        RunGpus,
        RunNetwork,
        RunStopTimeout,
        NetworkInternal,
        NetworkDriverOptions,
        NetworkAliases,
        ImageSave,
        ContainersSize,
        ContainersShowAll,
        ContainerAttach,
        ContainerExport,
        ContainerRestart,
        ContainerKill,
        WslPreRelease,
        StorageChangeLocation,
        RestartWslSession,
        ShutdownWsl,
        ComposeRebuild,
        KubernetesLifetime,
    ];

    private static FlagHelpEntry Entry(string key, string title, string flag, string text) =>
        new(key, title, flag, text);
}
