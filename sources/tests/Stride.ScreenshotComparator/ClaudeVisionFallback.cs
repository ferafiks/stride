// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Stride.Tests.ScreenshotComparator;

/// <summary>
/// Calls Claude Haiku 4.5 vision with the baseline(s) + capture and asks "is this the same scene?".
/// Used as a second-opinion fallback when LPIPS is over threshold but the test opted into
/// <c>claudeFallback</c>. When more than one baseline is provided they're framed as the
/// acceptable variance range for the frame. ANTHROPIC_API_KEY env var is required; if missing,
/// the fallback fails closed (returns Pass=false) so the regression sticks.
/// </summary>
public static class ClaudeVisionFallback
{
    private const string Model = "claude-haiku-4-5";
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";

    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public readonly record struct Verdict(bool Pass, string Reason);

    /// <summary>Single-baseline overload — back-compat shim around the multi-baseline form.</summary>
    public static Verdict Compare(string baselinePath, string capturePath, ComparisonPrompt prompt)
        => Compare(new[] { baselinePath }, capturePath, prompt);

    public static Verdict Compare(IReadOnlyList<string> baselinePaths, string capturePath, ComparisonPrompt prompt)
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
            return new Verdict(false, "ANTHROPIC_API_KEY not set");
        if (baselinePaths.Count == 0)
            return new Verdict(false, "no baselines provided");

        var captureB64 = Convert.ToBase64String(File.ReadAllBytes(capturePath));
        var promptText = prompt.Build(baselinePaths.Count);

        var content = new List<object>();
        for (var i = 0; i < baselinePaths.Count; i++)
        {
            var label = baselinePaths.Count == 1 ? "BASELINE:" : $"BASELINE {i + 1} of {baselinePaths.Count}:";
            var b64 = Convert.ToBase64String(File.ReadAllBytes(baselinePaths[i]));
            content.Add(new { type = "text", text = label });
            content.Add(new { type = "image", source = new { type = "base64", media_type = "image/png", data = b64 } });
        }
        content.Add(new { type = "text", text = "CAPTURE:" });
        content.Add(new { type = "image", source = new { type = "base64", media_type = "image/png", data = captureB64 } });
        content.Add(new { type = "text", text = promptText });

        var body = JsonSerializer.Serialize(new
        {
            model = Model,
            max_tokens = 80,
            temperature = 0.0,
            messages = new[]
            {
                new { role = "user", content = content.ToArray() },
            },
        });

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", ApiVersion);

            using var resp = http.Send(req);
            var respBody = resp.Content.ReadAsStringAsync().Result;
            if (!resp.IsSuccessStatusCode)
                return new Verdict(false, $"claude api {(int)resp.StatusCode}: {Truncate(respBody, 200)}");

            using var doc = JsonDocument.Parse(respBody);
            // Response shape: { content: [{ type: "text", text: "YES: ..." | "NO: ..." }] }
            var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
            text = text.Trim();
            // Accept "YES" or "NO" prefix (case-insensitive).
            var pass = text.StartsWith("YES", StringComparison.OrdinalIgnoreCase);
            return new Verdict(pass, text);
        }
        catch (Exception ex)
        {
            return new Verdict(false, $"claude error: {ex.Message}");
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
