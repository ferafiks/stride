// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

// GameStudio capture for the "create a new game" flow: pick the New Game template in
// ProjectSelectionWindow, accept GameTemplateWindow defaults, wait for GameStudioWindow.
using System;
using System.Threading.Tasks;
using Stride.GameStudio.AutoTesting;

namespace Stride.Editor.Tests;

[UITest(SampleTemplateId = "81d2adea-37b1-4711-834c-0d73a05c206c")]
public class NewGameEditor : IUITest
{
    public async Task Run(IUITestContext ctx)
    {
        await ctx.WaitDispatcherIdle();

        // The /NewProject arg shows ProjectSelectionWindow on launch — wait for it, pick the
        // New Game template, then proceed.
        if (!await ctx.WaitForWindow("ProjectSelectionWindow", timeoutSeconds: 30))
        {
            ctx.Exit(1);
            return;
        }
        await Task.Delay(TimeSpan.FromSeconds(1)); // let templates panel populate

        if (!await ctx.SelectTemplate("81d2adea-37b1-4711-834c-0d73a05c206c"))
        {
            ctx.Exit(1);
            return;
        }
        await ctx.WaitFrames(2);

        // Click OK on ProjectSelectionWindow → triggers PrepareForRun on NewGameTemplateGenerator
        // which shows GameTemplateWindow (parameter dialog). Defaults are usable; close with Ok.
        if (!await ctx.CloseModalWithOk("ProjectSelectionWindow")) { ctx.Exit(1); return; }
        if (!await ctx.WaitForWindow("GameTemplateWindow", timeoutSeconds: 30)) { ctx.Exit(1); return; }
        if (!await ctx.CloseModalWithOk("GameTemplateWindow")) { ctx.Exit(1); return; }

        // Project generation runs (creates .sln, .csproj, asset folders, restores NuGet).
        // Then the editor opens it and GameStudioWindow appears.
        if (!await ctx.WaitForWindow("GameStudioWindow", timeoutSeconds: 180)) { ctx.Exit(1); return; }
        await ctx.SetWindowSize("GameStudioWindow", 2560, 1440);
        await ctx.WaitIdle();

        await ctx.Screenshot("new-game-editor");
        ctx.Exit();
    }
}
