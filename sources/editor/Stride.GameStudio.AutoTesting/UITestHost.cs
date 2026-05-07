// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Stride.Core.Assets.Editor.Components.TemplateDescriptions.ViewModels;
using Stride.Core.Assets.Editor.Components.TemplateDescriptions.Views;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Presentation.Controls;
using Stride.Core.Presentation.Services;
using Stride.Editor.Build;
using Stride.Editor.EditorGame.Game;
using Stride.Editor.Preview;
using Stride.GameStudio.ViewModels;

namespace Stride.GameStudio.AutoTesting;

/// <summary>
/// Loads the test DLL, polls for a settled WPF window, runs the chosen <see cref="IUITest"/>
/// fixture, and provides the <see cref="IUITestContext"/> impl (waits + WGC capture).
/// </summary>
internal sealed class UITestHost
{
    // Output dir suffix is the runtime monitor DPI percentage so per-DPI captures stay separate
    // (baselines are likewise stored under tests/editor/baselines/dpi<N>/). The runner detects
    // DPI at startup via GetDpiForMonitor(MDT_EFFECTIVE_DPI), which returns the user-set scale
    // factor regardless of process DPI-awareness.
    private const string OutDirNamePrefix = "ui-test-out-dpi";
    private const string ScreenshotsDir = "screenshots";
    private const string DoneFileName = "done.json";
    private const string LogFileName = "log.txt";

    // Window types that indicate the editor has finished startup; transients like
    // WorkProgressWindow are intentionally excluded.
    private static readonly HashSet<string> ReadyWindowTypeNames = new(StringComparer.Ordinal)
    {
        "GameStudioWindow",
        "ProjectSelectionWindow",
    };

    private readonly Dispatcher dispatcher;
    private readonly string testDllPath;
    private readonly string? testClassName;
    private readonly string outputDir;
    private StreamWriter? logWriter;
    private readonly List<string> capturedNames = new();
    private string lastSeenWindowsSummary = "";

    public int ExitCode { get; private set; }

    public UITestHost(Dispatcher dispatcher, string testDllPath, string? testClassName)
    {
        this.dispatcher = dispatcher;
        this.testDllPath = testDllPath;
        this.testClassName = testClassName;
        outputDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(testDllPath))!, OutDirNamePrefix + DpiUtil.DetectDpiPercent());
        Directory.CreateDirectory(Path.Combine(outputDir, ScreenshotsDir));
        try { logWriter = new StreamWriter(new FileStream(Path.Combine(outputDir, LogFileName), FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true }; }
        catch (Exception ex) { Console.Error.WriteLine($"UITestHost: failed to open log: {ex.Message}"); }
    }

    public void Start()
    {
        Log($"Start: testDllPath={testDllPath} testClassName={testClassName ?? "(auto)"}");
        var test = LoadTest();
        Log($"Test loaded: {test.GetType().FullName}");
        var ctx = new Context(this);

        // Background polling loop that marshals each window check onto the dispatcher.
        var fired = false;
        Task.Run(async () =>
        {
            await Task.Delay(2000).ConfigureAwait(false);
            for (var i = 0; i < 1500; i++)
            {
                if (fired) return;
                bool ready;
                try { ready = await dispatcher.InvokeAsync(HasReadyWindow).Task.ConfigureAwait(false); }
                catch (Exception ex) { Log($"Poll: InvokeAsync failed: {ex.Message}"); return; }
                if (ready)
                {
                    fired = true;
                    await dispatcher.InvokeAsync(() => RunTest(test, ctx)).Task.ConfigureAwait(false);
                    return;
                }
                await Task.Delay(200).ConfigureAwait(false);
            }
            Log("Poll: gave up after 1500 iterations (~5min).");
        });
    }

    private void Log(string message)
    {
        var line = $"{DateTime.UtcNow:HH:mm:ss.fff} {message}";
        Console.Error.WriteLine(line);
        try { logWriter?.WriteLine(line); }
        catch { /* best-effort */ }
    }

    private bool HasReadyWindow()
    {
        var app = Application.Current;
        if (app is null) return false;
        var summary = string.Join(", ", app.Windows.OfType<Window>().Select(w =>
            $"{w.GetType().Name}[Title='{w.Title}'](visible={w.IsVisible},loaded={w.IsLoaded},{w.ActualWidth}x{w.ActualHeight})"));
        if (summary != lastSeenWindowsSummary)
        {
            Log($"windows: {summary}");
            lastSeenWindowsSummary = summary;
        }
        foreach (var win in app.Windows.OfType<Window>())
        {
            if (!win.IsVisible || !win.IsLoaded) continue;
            if (win.ActualWidth < 100 || win.ActualHeight < 100) continue;
            if (!ReadyWindowTypeNames.Contains(win.GetType().Name)) continue;
            Log($"ready: '{win.GetType().Name}' Title='{win.Title}' Size={win.ActualWidth}x{win.ActualHeight}");
            return true;
        }
        return false;
    }

    private IUITest LoadTest()
    {
        var asm = Assembly.LoadFrom(testDllPath);
        var candidates = asm.GetTypes()
            .Where(t => t.GetCustomAttribute<UITestAttribute>() is not null)
            .ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException($"No [UITest] class found in '{testDllPath}'.");

        Type chosen;
        if (testClassName is not null)
        {
            chosen = candidates.FirstOrDefault(t => t.Name == testClassName || t.FullName == testClassName)
                ?? throw new InvalidOperationException($"No [UITest] class named '{testClassName}' in '{testDllPath}'. Available: {string.Join(", ", candidates.Select(t => t.FullName))}");
        }
        else if (candidates.Count == 1)
        {
            chosen = candidates[0];
        }
        else
        {
            throw new InvalidOperationException($"Multiple [UITest] classes in '{testDllPath}'; pass --test-name to select. Available: {string.Join(", ", candidates.Select(t => t.FullName))}");
        }

        if (!typeof(IUITest).IsAssignableFrom(chosen))
            throw new InvalidOperationException($"[UITest] class {chosen.FullName} must implement IUITest.");

        return (IUITest)Activator.CreateInstance(chosen)!;
    }

    private void RunTest(IUITest test, Context ctx)
    {
        Task.Run(async () =>
        {
            var status = "ok";
            object? exceptionInfo = null;
            try
            {
                await test.Run(ctx);
            }
            catch (Exception ex)
            {
                status = "error";
                exceptionInfo = SerializeException(ex);
                Console.Error.WriteLine(ex);
                ExitCode = 1;
            }
            finally
            {
                WriteDoneJson(status, exceptionInfo);
                ctx.ShutdownInternal();
            }
        });
    }

    private void WriteDoneJson(string status, object? exceptionInfo)
    {
        try
        {
            var donePath = Path.Combine(outputDir, DoneFileName);
            // claudeFallback=true on every editor frame: the editor UI naturally drifts
            // (asset-thumbnail render order, scroll positions, scene-camera framing) and LPIPS
            // alone produces too many false positives. Claude vision only fires on frames that
            // already exceed the LPIPS threshold, so cost is bounded.
            var payload = new
            {
                status,
                screenshots = capturedNames.Select(n => new { name = n, claudeFallback = true }),
                exception = exceptionInfo,
            };
            File.WriteAllText(donePath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"UITestHost: failed to write done.json: {ex}");
        }
    }

    private static object SerializeException(Exception ex) => new
    {
        type = ex.GetType().FullName,
        message = ex.Message,
        stack = ex.ToString(),
    };

    private sealed class Context : IUITestContext
    {
        private readonly UITestHost host;
        public Context(UITestHost host) { this.host = host; }

        public Task OpenProject() => Task.CompletedTask; // project is loaded by GameStudio's positional-arg path before the test runs

        public Task WaitForAssetBuild() => WaitForQueueDrained("asset-build", () =>
        {
            var session = TryGetSession();
            return session?.ServiceProvider.TryGet<AssetBuilderService>()?.QueuedBuildUnitCount ?? 0;
        });

        public Task WaitForShaders() => WaitForQueueDrained("shader-compile", () =>
        {
            var session = TryGetSession();
            return session?.ServiceProvider.TryGet<GameStudioBuilderService>()?.PendingShaderCompilationCount ?? 0;
        });

        public Task WaitDispatcherIdle()
        {
            var tcs = new TaskCompletionSource();
            host.dispatcher.BeginInvoke(() => tcs.SetResult(), DispatcherPriority.ApplicationIdle);
            return tcs.Task;
        }

        public async Task WaitFrames(int n = 1)
        {
            for (var i = 0; i < n; i++)
                await WaitDispatcherIdle();
        }

        public async Task WaitIdle()
        {
            await WaitForAssetBuild();
            await WaitForShaders();
            await WaitDispatcherIdle();
            await WaitFrames(1);
            await WaitForRendering();
        }

        public async Task WaitForRendering(int frames = 60, double timeoutSeconds = 30)
        {
            // Snapshot the (game, startFrameCount) pairs on the dispatcher; reading EditorServiceGame
            // state from the WPF UI thread is the safe path. PreviewGame lives on its own thread but
            // its DrawTime.FrameCount property is just an int read.
            var watched = await host.dispatcher.InvokeAsync(EnumerateActiveGames).Task.ConfigureAwait(false);
            if (watched.Count == 0)
            {
                host.Log("WaitForRendering: no active EditorServiceGame instances — skipping");
                return;
            }
            host.Log($"WaitForRendering: watching {watched.Count} game(s) for ≥{frames} frames each (timeout {timeoutSeconds}s)");

            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100).ConfigureAwait(false);
                var allReady = true;
                foreach (var w in watched)
                {
                    int current;
                    try { current = w.Game.DrawTime?.FrameCount ?? w.StartFrame; }
                    catch { continue; }  // game disposed mid-wait — treat as ready (drop from watch list semantically)
                    if (current - w.StartFrame < frames) { allReady = false; break; }
                }
                if (allReady)
                {
                    host.Log($"WaitForRendering: all watched games advanced ≥{frames} frames");
                    return;
                }
            }
            var snapshot = string.Join(", ", watched.Select(w =>
            {
                int? cur; try { cur = w.Game.DrawTime?.FrameCount; } catch { cur = null; }
                return $"{w.Game.GetType().Name}={(cur is null ? "?" : (cur - w.StartFrame).ToString())}";
            }));
            host.Log($"WaitForRendering: timed out after {timeoutSeconds}s — advances {snapshot}");
        }

        private readonly record struct WatchedGame(EditorServiceGame Game, int StartFrame);

        /// <summary>
        /// Walks the session's preview service + open asset-editor list and returns one entry per
        /// running <see cref="EditorServiceGame"/> with its current <see cref="GameTime.FrameCount"/>.
        /// Reflection-based: <c>AssetEditorsManager.assetEditors</c> is private, and
        /// <c>EditorGameController&lt;T&gt;.Game</c> is a protected field — neither is reachable from
        /// the AutoTesting assembly without [InternalsVisibleTo], which we don't need to add for
        /// this read-only diagnostic walk.
        /// </summary>
        private List<WatchedGame> EnumerateActiveGames()
        {
            var list = new List<WatchedGame>();
            var session = TryGetSession();
            if (session is null) return list;

            // 1) Shared asset-preview game (runs on its own STA thread, drives thumbnail rendering).
            var previewSvc = session.ServiceProvider.TryGet<GameStudioPreviewService>();
            if (previewSvc?.PreviewGame is { IsRunning: true } previewGame)
                list.Add(new WatchedGame(previewGame, previewGame.DrawTime?.FrameCount ?? 0));

            // 2) Each open asset editor's embedded game. Multiple scene/prefab/UI/sprite documents
            //    can be open simultaneously — collect each one's running game.
            var aem = session.ServiceProvider.TryGet<IAssetEditorsManager>();
            if (aem is null) return list;
            var assetEditorsField = aem.GetType().GetField("assetEditors", BindingFlags.NonPublic | BindingFlags.Instance);
            if (assetEditorsField?.GetValue(aem) is not IDictionary assetEditors) return list;
            foreach (var editorVm in assetEditors.Keys)
            {
                if (editorVm is null) continue;
                try
                {
                    // Walk declared-only because SceneEditorViewModel etc. shadow GameEditorViewModel.Controller
                    // with a more-derived return type — a flat GetProperty("Controller") triggers AmbiguousMatchException.
                    var controllerProp = FindMemberDeclaredOnly(editorVm.GetType(), t => t.GetProperty("Controller",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
                    if (controllerProp?.GetValue(editorVm) is not { } controller) continue;
                    var gameField = FindMemberDeclaredOnly(controller.GetType(), t => t.GetField("Game",
                        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));
                    if (gameField?.GetValue(controller) is EditorServiceGame game && game.IsRunning)
                        list.Add(new WatchedGame(game, game.DrawTime?.FrameCount ?? 0));
                }
                catch (Exception ex)
                {
                    host.Log($"WaitForRendering: skipping editor '{editorVm.GetType().Name}': {ex.GetType().Name}: {ex.Message}");
                }
            }
            return list;
        }

        /// <summary>
        /// Walks <paramref name="t"/> + base types, returning the first member found by <paramref name="lookup"/>.
        /// Caller passes a DeclaredOnly lookup so each level is searched independently — sidesteps
        /// AmbiguousMatchException from new-slot/shadowed members across the hierarchy.
        /// </summary>
        private static T? FindMemberDeclaredOnly<T>(Type? t, Func<Type, T?> lookup) where T : class
        {
            while (t is not null)
            {
                var hit = lookup(t);
                if (hit is not null) return hit;
                t = t.BaseType;
            }
            return null;
        }

        /// <summary>
        /// Returns when <paramref name="getCount"/> reads zero on two consecutive idle ticks.
        /// The two-tick rule absorbs the race where one drain seeds the queue from a follow-up.
        /// </summary>
        private async Task WaitForQueueDrained(string label, Func<int> getCount)
        {
            const int RequiredStableTicks = 2;
            var deadline = DateTime.UtcNow.AddSeconds(120);
            var stable = 0;
            var lastLogged = -1;
            var nextLogAt = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                await WaitDispatcherIdle();
                var count = await host.dispatcher.InvokeAsync(getCount, DispatcherPriority.ApplicationIdle);
                if (DateTime.UtcNow >= nextLogAt && count != lastLogged)
                {
                    host.Log($"WaitForQueueDrained('{label}') count={count}");
                    lastLogged = count;
                    nextLogAt = DateTime.UtcNow.AddSeconds(2);
                }
                if (count == 0)
                {
                    if (++stable >= RequiredStableTicks) return;
                }
                else
                {
                    stable = 0;
                }
            }
            host.Log($"WaitForQueueDrained('{label}') timed out after 120s.");
        }

        private static SessionViewModel? TryGetSession()
        {
            var app = Application.Current;
            if (app is null) return null;
            foreach (var w in app.Windows.OfType<Window>())
            {
                if (w.DataContext is GameStudioViewModel gs) return gs.Session;
            }
            return null;
        }

        public async Task Screenshot(string name)
        {
            var window = await host.dispatcher.InvokeAsync(ResolveCaptureWindow).Task.ConfigureAwait(false);
            if (window is null)
            {
                host.Log("Screenshot: no window to capture.");
                return;
            }
            var (winInfo, hwnd) = await host.dispatcher.InvokeAsync(() =>
                ($"'{window.GetType().Name}' Title='{window.Title}' Size={window.ActualWidth}x{window.ActualHeight}",
                 new WindowInteropHelper(window).Handle)).Task.ConfigureAwait(false);
            host.Log($"Screenshot: capturing {winInfo}");

            // Force a fresh WPF render so DWM has a frame for WGC to capture.
            await host.dispatcher.InvokeAsync(() =>
            {
                window.Activate();
                window.InvalidateVisual();
                window.UpdateLayout();
            }, DispatcherPriority.Normal).Task.ConfigureAwait(false);
            await host.dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render).Task.ConfigureAwait(false);
            await Task.Delay(150).ConfigureAwait(false);

            try
            {
                var path = Path.Combine(host.outputDir, ScreenshotsDir, name + ".png");
                if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Window has no HWND yet.");
                await GraphicsCaptureClient.CaptureToPngAsync(hwnd, path).ConfigureAwait(false);
                host.capturedNames.Add(name);
            }
            catch (Exception ex)
            {
                host.Log($"Screenshot('{name}') failed: {ex}");
            }
        }

        public async Task<bool> WaitForWindow(string windowTypeName, double timeoutSeconds = 120)
        {
            host.Log($"WaitForWindow: '{windowTypeName}' (timeout={timeoutSeconds}s)");
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                var found = await host.dispatcher.InvokeAsync(() =>
                {
                    var app = Application.Current;
                    return app?.Windows.OfType<Window>().Any(w =>
                        w.GetType().Name == windowTypeName && w.IsVisible && w.IsLoaded
                        && w.ActualWidth >= 100 && w.ActualHeight >= 100) ?? false;
                }).Task.ConfigureAwait(false);
                if (found)
                {
                    host.Log($"WaitForWindow: '{windowTypeName}' ready");
                    return true;
                }
                await Task.Delay(200).ConfigureAwait(false);
            }
            host.Log($"WaitForWindow: '{windowTypeName}' timed out after {timeoutSeconds}s");
            return false;
        }

        public Task<bool> SelectTemplate(string templateGuid) =>
            host.dispatcher.InvokeAsync(() =>
            {
                host.Log($"SelectTemplate: '{templateGuid}'");
                var app = Application.Current;
                if (app is null) return false;
                var win = app.Windows.OfType<ProjectSelectionWindow>().FirstOrDefault();
                if (win is null) { host.Log("SelectTemplate: ProjectSelectionWindow not found"); return false; }
                var collection = win.Templates;
                if (collection is null) { host.Log("SelectTemplate: ProjectSelectionWindow.Templates is null"); return false; }
                if (!Guid.TryParse(templateGuid, out var targetId))
                { host.Log($"SelectTemplate: '{templateGuid}' is not a valid GUID"); return false; }
                // Templates is the per-group filtered view; full set lives behind RootGroups.
                var candidates = collection.Templates
                    .Concat(collection.RootGroups.SelectMany(g => g.GetTemplatesRecursively()))
                    .ToList();
                var match = candidates.FirstOrDefault(t => t.Id == targetId);
                if (match is null)
                { host.Log($"SelectTemplate: no template with Id={templateGuid} in {candidates.Count} candidates"); return false; }
                collection.SelectedTemplate = match;
                host.Log($"SelectTemplate: selected '{match.GetType().Name}' (Id={match.Id})");
                return true;
            }).Task;

        public Task<bool> CloseModalWithOk(string windowTypeName) =>
            host.dispatcher.InvokeAsync(() =>
            {
                host.Log($"CloseModalWithOk: '{windowTypeName}'");
                var app = Application.Current;
                if (app is null) return false;
                var win = app.Windows.OfType<Window>().FirstOrDefault(w => w.GetType().Name == windowTypeName);
                if (win is null) { host.Log($"CloseModalWithOk: '{windowTypeName}' not found"); return false; }
                if (win is ModalWindow modal)
                {
                    modal.RequestClose(DialogResult.Ok);
                    host.Log($"CloseModalWithOk: RequestClose(Ok) on '{windowTypeName}'");
                    return true;
                }
                win.Close();
                host.Log($"CloseModalWithOk: Close() on '{windowTypeName}' (not a ModalWindow)");
                return true;
            }).Task;

        public async Task SetWindowSize(string windowTypeName, int width, int height) =>
            await host.dispatcher.InvokeAsync(() =>
            {
                var work = SystemParameters.WorkArea;
                // Clamp to work area so the window stays fully on-screen — partially off-screen
                // windows confuse DWM redirection and break WGC capture downstream.
                var w = Math.Min(width, (int)work.Width);
                var h = Math.Min(height, (int)work.Height);
                host.Log($"SetWindowSize: '{windowTypeName}' → req {width}x{height} clamped {w}x{h} (work={work.Width}x{work.Height})");
                var win = Application.Current?.Windows.OfType<Window>()
                    .FirstOrDefault(w0 => w0.GetType().Name == windowTypeName);
                if (win is null) { host.Log($"SetWindowSize: '{windowTypeName}' not found"); return; }
                win.SetCurrentValue(Window.WindowStateProperty, WindowState.Normal);
                win.SetCurrentValue(Window.SizeToContentProperty, SizeToContent.Manual);
                win.SetCurrentValue(Window.WidthProperty, (double)w);
                win.SetCurrentValue(Window.HeightProperty, (double)h);
                win.SetCurrentValue(Window.LeftProperty, work.Left + Math.Max(0.0, (work.Width - w) / 2.0));
                win.SetCurrentValue(Window.TopProperty, work.Top + Math.Max(0.0, (work.Height - h) / 2.0));
                win.UpdateLayout();
                host.Log($"SetWindowSize: after — Width={win.Width} Height={win.Height} Actual={win.ActualWidth}x{win.ActualHeight} State={win.WindowState}");
            }, DispatcherPriority.Render).Task.ConfigureAwait(false);

        public async Task CapturePanel(string idOrTitle, string name, int width = 1200, int height = 900)
        {
            host.Log($"CapturePanel: id='{idOrTitle}' name='{name}' size={width}x{height}");
            var path = Path.Combine(host.outputDir, ScreenshotsDir, name + ".png");
            object? anchorable = null;
            AnchorableState? originalState = null;
            try
            {
                anchorable = await host.dispatcher.InvokeAsync(() => FindAnchorable(idOrTitle)).Task.ConfigureAwait(false);
                if (anchorable is null) { host.Log($"CapturePanel: '{idOrTitle}' not found."); return; }

                originalState = await host.dispatcher.InvokeAsync(() => FloatAnchorable(anchorable, width, height)).Task.ConfigureAwait(false);

                // Let the floating window realize and lay out.
                await WaitDispatcherIdle();
                await Task.Delay(250).ConfigureAwait(false);
                await WaitDispatcherIdle();

                await host.dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render).Task.ConfigureAwait(false);
                await Task.Delay(150).ConfigureAwait(false);

                var (winInfo, hwnd) = await host.dispatcher.InvokeAsync(() =>
                {
                    var floatWin = FindFloatingWindow(idOrTitle)
                        ?? throw new InvalidOperationException($"Floating window for '{idOrTitle}' not found after Float().");
                    floatWin.UpdateLayout();
                    return ($"'{floatWin.GetType().Name}' Size={floatWin.ActualWidth}x{floatWin.ActualHeight}",
                            new WindowInteropHelper(floatWin).Handle);
                }).Task.ConfigureAwait(false);
                host.Log($"CapturePanel: capturing {winInfo}");
                if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Floating window has no HWND yet.");

                // WGC captures DWM composition output, including D3DImage interop content like the
                // embedded scene preview's swap-chain.
                await GraphicsCaptureClient.CaptureToPngAsync(hwnd, path).ConfigureAwait(false);
                host.Log($"CapturePanel: wrote → {path}");
                host.capturedNames.Add(name);
            }
            catch (Exception ex)
            {
                host.Log($"CapturePanel('{idOrTitle}','{name}') failed: {ex}");
            }
            finally
            {
                if (anchorable is not null && originalState is not null)
                {
                    try
                    {
                        await host.dispatcher.InvokeAsync(() => RestoreAnchorable(anchorable, originalState.Value)).Task.ConfigureAwait(false);
                    }
                    catch (Exception ex) { host.Log($"CapturePanel: restore failed: {ex}"); }
                }
            }
        }

        /// <summary>
        /// Walks <see cref="Application.Windows"/> and returns the first top-level Window whose visual
        /// tree contains the LayoutAnchorable for <paramref name="contentId"/> — i.e. the floating
        /// window AvalonDock spawned by <c>Float()</c>. Skips the main GameStudioWindow.
        /// </summary>
        private static Window? FindFloatingWindow(string contentId)
        {
            var app = Application.Current;
            if (app is null) return null;
            foreach (var w in app.Windows.OfType<Window>())
            {
                if (w.GetType().Name == "GameStudioWindow") continue;
                if (SearchTree(w, contentId, returnElement: false) is not null)
                    return w;
            }
            return null;
        }

        /// <summary>Finds the FrameworkElement hosting an AvalonDock panel by <c>ContentId</c>.</summary>
        private static FrameworkElement? FindPanelContent(string contentId)
        {
            var app = Application.Current;
            if (app is null) return null;
            foreach (var w in app.Windows.OfType<Window>())
            {
                var hit = SearchTree(w, contentId, returnElement: true) as FrameworkElement;
                if (hit is not null) return hit;
            }
            return null;
        }

        /// <summary>Finds the AvalonDock LayoutAnchorable (DataContext object with the matching <c>ContentId</c>).</summary>
        private static object? FindAnchorable(string contentId)
        {
            var app = Application.Current;
            if (app is null) return null;
            foreach (var w in app.Windows.OfType<Window>())
            {
                var hit = SearchTree(w, contentId, returnElement: false);
                if (hit is not null) return hit;
            }
            return null;
        }

        private static object? SearchTree(DependencyObject node, string idOrTitle, bool returnElement)
        {
            if (node is FrameworkElement fe && fe.DataContext is { } dc)
            {
                var t = dc.GetType();
                // Anchorables (panels) match by ContentId; documents (asset editors) typically have
                // an empty ContentId and identify via Title (the asset URL).
                if (t.GetProperty("ContentId")?.GetValue(dc) is string cid && string.Equals(cid, idOrTitle, StringComparison.Ordinal))
                    return returnElement ? fe : dc;
                if (t.GetProperty("Title")?.GetValue(dc) is string title && string.Equals(title, idOrTitle, StringComparison.Ordinal))
                    return returnElement ? fe : dc;
            }
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                var hit = SearchTree(child, idOrTitle, returnElement);
                if (hit is not null) return hit;
            }
            return null;
        }

        private readonly record struct AnchorableState(bool WasAutoHidden, bool WasFloating, double OldFloatingWidth, double OldFloatingHeight);

        private static AnchorableState FloatAnchorable(object anchorable, int width, int height)
        {
            var t = anchorable.GetType();
            var wasAutoHidden = (bool?)t.GetProperty("IsAutoHidden")?.GetValue(anchorable) ?? false;
            var wasFloating = (bool?)t.GetProperty("IsFloating")?.GetValue(anchorable) ?? false;
            var oldFW = (double?)t.GetProperty("FloatingWidth")?.GetValue(anchorable) ?? 0;
            var oldFH = (double?)t.GetProperty("FloatingHeight")?.GetValue(anchorable) ?? 0;
            // Auto-hidden panels need to be expanded before Float() can move them, otherwise the
            // anchorable is still parented to the auto-hide pane and the call no-ops.
            if (wasAutoHidden) t.GetMethod("ToggleAutoHide")?.Invoke(anchorable, null);
            t.GetProperty("FloatingWidth")?.SetValue(anchorable, (double)width);
            t.GetProperty("FloatingHeight")?.SetValue(anchorable, (double)height);
            if (!wasFloating) t.GetMethod("Float")?.Invoke(anchorable, null);
            return new AnchorableState(wasAutoHidden, wasFloating, oldFW, oldFH);
        }

        private static void RestoreAnchorable(object anchorable, AnchorableState state)
        {
            var t = anchorable.GetType();
            if (!state.WasFloating) t.GetMethod("Dock")?.Invoke(anchorable, null);
            t.GetProperty("FloatingWidth")?.SetValue(anchorable, state.OldFloatingWidth);
            t.GetProperty("FloatingHeight")?.SetValue(anchorable, state.OldFloatingHeight);
            var nowAutoHidden = (bool?)t.GetProperty("IsAutoHidden")?.GetValue(anchorable) ?? false;
            if (state.WasAutoHidden && !nowAutoHidden) t.GetMethod("ToggleAutoHide")?.Invoke(anchorable, null);
        }

        public void Exit(int newExitCode = 0)
        {
            host.ExitCode = newExitCode;
            ShutdownInternal();
        }

        public void ShutdownInternal()
        {
            host.dispatcher.BeginInvoke(() =>
            {
                Environment.ExitCode = host.ExitCode;
                var app = Application.Current;
                if (app is null) return;
                foreach (var win in app.Windows.Cast<Window>().ToList())
                {
                    try { win.Close(); } catch { /* best-effort */ }
                }
                app.Shutdown(host.ExitCode);
            });
        }

        private static Window? ResolveCaptureWindow()
        {
            var app = Application.Current;
            if (app is null) return null;
            return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                ?? app.Windows.OfType<Window>().LastOrDefault(w => w.IsLoaded)
                ?? app.MainWindow;
        }
    }
}
