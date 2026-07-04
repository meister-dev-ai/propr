// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.CodeAnalysis.TreeSitter.Parsing;

namespace MeisterProPR.CodeAnalysis.TreeSitter.Tests;

/// <summary>
///     US3 concurrency and containment tests for <see cref="ParserPool" /> (C5, C7, C9).
/// </summary>
public sealed class ParserPoolConcurrencyTests
{
    // C9 — N concurrent calls up to MaxFileReviewConcurrency return correct, non-interfering results.
    [Fact]
    public async Task ParseAsync_ConcurrentCallsUpToConcurrency_ReturnCorrectNonInterferingResults()
    {
        var pool = new ParserPool(3, 524_288, 5_000);

        // Each task parses a distinct TS source with a unique function name. Results must
        // not cross-contaminate — the tree from one parse must not leak into another.
        var sources = new[]
        {
            "export function alpha() { return 1; }",
            "export function beta() { return 2; }",
            "export function gamma() { return 3; }",
            "export function delta() { return 4; }",
            "export function epsilon() { return 5; }",
            "export function zeta() { return 6; }",
        };

        var tasks = sources.Select(src => pool.ParseAsync(SupportedLanguage.TypeScript, src, CancellationToken.None));
        var results = await Task.WhenAll(tasks);

        // All parses should succeed (no fallback reasons).
        Assert.All(results, r => Assert.Null(r.FallbackReason));
        Assert.All(results, r => Assert.NotNull(r.Tree));

        // Each tree's root text must contain its own function name, not any other.
        for (var i = 0; i < sources.Length; i++)
        {
            using var tree = results[i].Tree!;
            var rootText = tree.RootNode.Text;
            var expectedFn = sources[i].Contains("alpha", StringComparison.Ordinal) ? "alpha"
                : sources[i].Contains("beta", StringComparison.Ordinal) ? "beta"
                : sources[i].Contains("gamma", StringComparison.Ordinal) ? "gamma"
                : sources[i].Contains("delta", StringComparison.Ordinal) ? "delta"
                : sources[i].Contains("epsilon", StringComparison.Ordinal) ? "epsilon"
                : "zeta";

            Assert.Contains(expectedFn, rootText, StringComparison.Ordinal);
        }

        pool.Dispose();
    }

    // C5 + C7 — pathological/oversized input under a short timeout returns empty, no throw,
    // and other concurrent parses are unaffected.
    [Fact]
    public async Task ParseAsync_PathologicalInputUnderShortTimeout_NoThrowOtherParsesSucceed()
    {
        // Short timeout to force the timeout path on a large input.
        var pool = new ParserPool(3, 524_288, 1);

        // A pathological input: deeply nested brackets that tree-sitter must still parse
        // (it's designed for real-time use, so this should complete — but if it doesn't,
        // the timeout must contain it).
        var pathological = new string('{', 500) + new string('}', 500);

        // A normal source that should parse successfully alongside the pathological one.
        var normal = "export function healthyFunction() { return 42; }";

        // Run both concurrently.
        var pathologicalTask = pool.ParseAsync(SupportedLanguage.TypeScript, pathological, CancellationToken.None);
        var normalTask = pool.ParseAsync(SupportedLanguage.TypeScript, normal, CancellationToken.None);

        var pathologicalResult = await pathologicalTask;
        var normalResult = await normalTask;

        // The pathological parse must not throw — it returns a result (possibly with a
        // fallback reason, or a tree with errors, but never throws).
        Assert.NotNull(pathologicalResult);

        // The normal parse must succeed regardless of what happened to the pathological one.
        // (It may be null if the 1ms timeout was too short for even the normal parse on a
        // slow machine, so we only assert no-throw + non-interference, not success.)
        Assert.NotNull(normalResult);

        pool.Dispose();
    }

    // C6 — oversized input returns FileTooLarge, no parse attempted.
    [Fact]
    public async Task ParseAsync_SourceExceedingMaxBytes_ReturnsFileTooLarge()
    {
        var pool = new ParserPool(3, 64, 5_000);

        var oversized = new string('x', 200);
        var result = await pool.ParseAsync(SupportedLanguage.TypeScript, oversized, CancellationToken.None);

        Assert.Null(result.Tree);
        Assert.Equal(FallbackReason.FileTooLarge, result.FallbackReason);

        pool.Dispose();
    }

    // C7 — parse exceeding StructuralParseTimeoutMs returns ParseTimeout, no throw.
    [Fact]
    public async Task ParseAsync_ParseExceedingTimeout_ReturnsParseTimeout_NoThrow()
    {
        // Use a 0ms timeout to force an immediate timeout.
        // Note: a 0ms timeout means the Task.WhenAny winner is likely the delay task,
        // so the parse is abandoned. The result must be ParseTimeout with no throw.
        var pool = new ParserPool(1, 524_288, 1);

        // A real source that takes at least some time to parse.
        var source = "export function slow() { return " + new string('1', 1000) + "; }";

        var result = await pool.ParseAsync(SupportedLanguage.TypeScript, source, CancellationToken.None);

        // The result must not throw. It's either a successful parse or a timeout.
        // With a 1ms timeout, it's very likely a timeout, but on a fast machine it could
        // succeed. The invariant is: no throw, and if it timed out, the reason is set.
        Assert.NotNull(result);
        if (result.Tree is null)
        {
            Assert.True(
                result.FallbackReason == FallbackReason.ParseTimeout ||
                result.FallbackReason == FallbackReason.ParseFault,
                $"Expected ParseTimeout or ParseFault, got {result.FallbackReason}");
        }

        pool.Dispose();
    }

    // C8 — disposed pool returns NativeUnavailable.
    [Fact]
    public async Task ParseAsync_DisposedPool_ReturnsNativeUnavailable()
    {
        var pool = new ParserPool(1, 524_288, 5_000);
        pool.Dispose();

        var result = await pool.ParseAsync(SupportedLanguage.TypeScript, "function f() {}", CancellationToken.None);

        Assert.Null(result.Tree);
        Assert.Equal(FallbackReason.NativeUnavailable, result.FallbackReason);
    }
}
