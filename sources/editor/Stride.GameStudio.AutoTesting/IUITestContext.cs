// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace Stride.GameStudio.AutoTesting;

/// <summary>
/// Wait/screenshot/exit primitives handed to the test fixture by the AutoTesting runner.
/// </summary>
public interface IUITestContext
{
    /// <summary>Opens the .sln pre-positioned on disk by the runner. No-op when no project test is configured.</summary>
    Task OpenProject();

    /// <summary>Returns when the editor's asset build queue is empty for two consecutive frames.</summary>
    Task WaitForAssetBuild();

    /// <summary>Returns when the shader compile queue is empty for two consecutive frames.</summary>
    Task WaitForShaders();

    /// <summary>Returns when the WPF dispatcher has drained to ApplicationIdle priority.</summary>
    Task WaitDispatcherIdle();

    /// <summary>Awaits N successive render frames.</summary>
    Task WaitFrames(int n = 1);

    /// <summary>Convenience: awaits asset build, shaders, dispatcher idle, one trailing frame, and rendering.</summary>
    Task WaitIdle();

    /// <summary>
    /// Returns when every active <c>EditorServiceGame</c> instance — the embedded scene/prefab/UI/sprite
    /// document games and the shared asset-preview game — has advanced its <c>DrawTime.FrameCount</c>
    /// by at least <paramref name="frames"/> since the call started, ensuring swap-chains have
    /// presented real content. No-op if no games are active. Times out after
    /// <paramref name="timeoutSeconds"/> with a log message — never throws.
    /// </summary>
    Task WaitForRendering(int frames = 60, double timeoutSeconds = 30);

    /// <summary>Captures the active main window to a PNG named <paramref name="name"/>.</summary>
    Task Screenshot(string name);

    /// <summary>
    /// Resizes a top-level window (looked up by class name) to a fixed <paramref name="width"/> ×
    /// <paramref name="height"/> and centers it on the primary screen. Used by fixtures to pin the
    /// main editor window to a deterministic capture size before <see cref="Screenshot"/>.
    /// </summary>
    Task SetWindowSize(string windowTypeName, int width, int height);

    /// <summary>
    /// Floats a single docked panel or document into its own window sized to <paramref name="width"/>
    /// × <paramref name="height"/>, captures it via WGC, then restores its original docked / auto-hide
    /// state. Lookup by <c>ContentId</c> for anchorable panels (e.g. "AssetView", "PropertyGrid",
    /// "SolutionExplorer", "BuildLog", "References") or by <c>Title</c> for asset-editor documents
    /// (e.g. "MainScene") which are added with empty ContentId and Title=asset.Url.
    /// </summary>
    Task CapturePanel(string idOrTitle, string name, int width = 1200, int height = 900);

    /// <summary>
    /// Polls the WPF Application.Windows set until a Window of class name
    /// <paramref name="windowTypeName"/> is visible and loaded, or <paramref name="timeoutSeconds"/>
    /// elapses. Returns true on success.
    /// </summary>
    Task<bool> WaitForWindow(string windowTypeName, double timeoutSeconds = 120);

    /// <summary>
    /// Selects a template in the ProjectSelectionWindow by template GUID and returns true if found.
    /// The dialog stays open; close it via <see cref="CloseModalWithOk"/>.
    /// </summary>
    Task<bool> SelectTemplate(string templateGuid);

    /// <summary>
    /// Closes a modal dialog with <c>DialogResult.Ok</c> (equivalent to clicking OK / Create).
    /// Returns true if the window was found and closed.
    /// </summary>
    Task<bool> CloseModalWithOk(string windowTypeName);

    /// <summary>Sets the process exit code and shuts the editor down.</summary>
    void Exit(int exitCode = 0);
}
