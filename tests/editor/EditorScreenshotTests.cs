// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Stride.GameStudio.AutoTesting;
using Stride.Tests.ScreenshotComparator;
using Xunit;
using Xunit.Abstractions;

namespace Stride.Editor.Tests;

/// <summary>
/// xunit orchestrator for the GameStudio editor screenshot regression pipeline. Each [Theory] entry
/// spawns Stride.GameStudio.AutoTesting.exe in a subprocess (one fixture per process for WPF
/// singleton-state isolation), waits for it to exit, then runs <see cref="ScreenshotComparator"/>
/// against committed baselines under <c>tests/editor/baselines/dpi100/&lt;fixture&gt;/&lt;frame&gt;.png</c>.
/// </summary>
[CollectionDefinition("EditorScreenshots", DisableParallelization = true)]
public class EditorScreenshotsCollection { }

[Collection("EditorScreenshots")]
public class EditorScreenshotTests
{
    // Detect the runtime DPI so capture and baseline directories are labeled honestly. Both this
    // test process and the AutoTesting runner subprocess call the same helper and converge on
    // the same string, so the snapshot/copy paths line up.
    private static readonly string Dpi = "dpi" + DpiUtil.DetectDpiPercent();

    private readonly ITestOutputHelper output;
    public EditorScreenshotTests(ITestOutputHelper output) => this.output = output;

    public static IEnumerable<object[]> Fixtures()
    {
        // (fixtureName, optional .sln path relative to worktree, timeout-minutes)
        yield return new object?[] { "EmptyEditor",   null,                                              3 };
        yield return new object?[] { "TopDownCreate", null,                                              8 };
        yield return new object?[] { "TopDownLoad",   "samples/Templates/TopDownRPG/TopDownRPG.sln",     5 };
        yield return new object?[] { "NewGameEditor", null,                                              5 };
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Capture(string fixtureName, string? slnPathRel, int timeoutMin)
    {
        var worktree = WorktreeRoot();
        var captureRoot = Path.Combine(worktree, "ui-test-out-" + Dpi);
        var fixtureCapture = Path.Combine(captureRoot, fixtureName);
        if (Directory.Exists(fixtureCapture))
            Directory.Delete(fixtureCapture, recursive: true);

        var dll = typeof(EditorScreenshotTests).Assembly.Location;
        var exe = ResolveAutoTestingExe(dll, worktree);
        var args = new List<string> { "--test-dll", dll, "--test-name", fixtureName };
        if (slnPathRel is not null) args.Add(Path.Combine(worktree, slnPathRel));

        // Clean the runner-side output dir so stale files from a previous fixture invocation
        // don't leak into this fixture's capture set.
        var runnerOut = Path.Combine(Path.GetDirectoryName(dll)!, "ui-test-out-" + Dpi);
        if (Directory.Exists(runnerOut)) Directory.Delete(runnerOut, recursive: true);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = worktree,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) output.WriteLine($"[stdout] {e.Data}"); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) output.WriteLine($"[stderr] {e.Data}"); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (!proc.WaitForExit(timeoutMin * 60_000))
        {
            try { proc.Kill(); } catch { }
            throw new TimeoutException($"{fixtureName} timed out after {timeoutMin}min");
        }
        Assert.True(proc.ExitCode == 0, $"{fixtureName} exit={proc.ExitCode}");

        // Snapshot per-fixture: the runner writes to <test-dll-dir>/ui-test-out-dpi100/{screenshots,
        // log.txt, done.json}; relocate that into <worktree>/ui-test-out-dpi100/<fixture>/ so the
        // comparator can find <newDir>/<sample>/screenshots/<frame>.png.
        if (!Directory.Exists(runnerOut))
            throw new DirectoryNotFoundException($"Runner output dir not found: {runnerOut}");
        Directory.CreateDirectory(fixtureCapture);
        CopyAll(runnerOut, fixtureCapture);

        // Diag logs live in $TEMP and are overwritten by each fixture's runner — copy them into
        // the per-fixture capture dir before the next [Theory] entry runs.
        var temp = Path.GetTempPath();
        foreach (var diag in new[] { "autotest-diag.log", "gs-diag.log" })
        {
            var src = Path.Combine(temp, diag);
            if (File.Exists(src))
            {
                try { File.Copy(src, Path.Combine(fixtureCapture, diag), overwrite: true); } catch { }
            }
        }

        // Compare against baselines. Filter to this fixture so the same captureRoot can host
        // multiple fixtures' captures across test invocations.
        var baselineDir = Path.Combine(worktree, "tests", "editor", "baselines", Dpi);
        var results = ScreenshotComparator.Compare(captureRoot, baselineDir,
            sampleFilter: fixtureName, defaultPrompt: EditorComparisonPrompt.Default);

        foreach (var r in results)
        {
            var lpips = r.Lpips.HasValue ? $"lpips={r.Lpips.Value:F4}" : "";
            output.WriteLine($"[compare] {r.Status,-12} {r.Frame,-25} thr={r.Threshold:F2} {lpips}{(r.Detail is null ? "" : "  " + r.Detail)}");
        }
        var failures = results.Where(r => r.Status is "drift" or "error" or "new").ToList();
        Assert.Empty(failures);
    }

    private static string ResolveAutoTestingExe(string testDllPath, string worktree)
    {
        // tests\editor\bin\<cfg>\<tfm>\Stride.Editor.Tests.dll → mirror the cfg+tfm into the runner's
        // sources\editor\Stride.GameStudio.AutoTesting\bin tree.
        var dllDir = new DirectoryInfo(Path.GetDirectoryName(testDllPath)!);
        var tfm = dllDir.Name;
        var cfg = dllDir.Parent!.Name;
        var path = Path.Combine(worktree, "sources", "editor", "Stride.GameStudio.AutoTesting",
            "bin", cfg, tfm, "Stride.GameStudio.AutoTesting.exe");
        if (!File.Exists(path))
            throw new FileNotFoundException($"AutoTesting runner exe not found at: {path}", path);
        return path;
    }

    private static void CopyAll(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.EnumerateDirectories(src))
            CopyAll(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    /// <summary>Walks up from cwd until it finds a NuGet.config — that's the worktree root.</summary>
    private static string WorktreeRoot()
    {
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NuGet.config")) || File.Exists(Path.Combine(dir.FullName, "nuget.config")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate worktree root from " + Environment.CurrentDirectory);
    }
}
