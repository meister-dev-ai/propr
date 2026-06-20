// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Options;
using MeisterProPR.CodeAnalysis.TreeSitter.Parsing;
using MeisterProPR.CodeAnalysis.TreeSitter.Startup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MeisterProPR.CodeAnalysis.TreeSitter.Tests;

/// <summary>
///     Shared factory helpers for analyzer tests.
/// </summary>
internal static class AnalyzerTestFactory
{
    /// <summary>
    ///     The 1-based start line of <c>deepTargetFunction</c> / equivalent deep function in
    ///     each fixture, plus its expected name. Used to assert C1 (inside-function resolves).
    /// </summary>
    public static readonly IReadOnlyDictionary<SupportedLanguage, (string Fixture, string Path, string DeepFunctionName, int DeepFunctionStartLine)>
        Fixtures = new Dictionary<SupportedLanguage, (string, string, string, int)>
        {
            [SupportedLanguage.TypeScript] = ("sample.ts", "sample.ts", "deepTargetFunction", 56),
            [SupportedLanguage.Tsx] = ("sample.tsx", "sample.tsx", "DeepItemList", 23),
            [SupportedLanguage.JavaScript] = ("sample.js", "sample.js", "deepTargetFunction", 41),
            [SupportedLanguage.Python] = ("sample.py", "sample.py", "deep_target_function", 42),
            [SupportedLanguage.Go] = ("sample.go", "sample.go", "deepTargetFunction", 45),
            [SupportedLanguage.Java] = ("Sample.java", "Sample.java", "deepTargetFunction", 41),
            [SupportedLanguage.Ruby] = ("sample.rb", "sample.rb", "deep_target_function", 41),
        };

    public static AiReviewOptions DefaultOptions(int maxParseBytes = 524_288, int parseTimeoutMs = 1_000)
    {
        return new AiReviewOptions
        {
            MaxFileReviewConcurrency = 3,
            MaxStructuralParseBytes = maxParseBytes,
            StructuralParseTimeoutMs = parseTimeoutMs,
        };
    }

    public static IOptions<AiReviewOptions> WrapOptions(AiReviewOptions? options = null)
    {
        return Options.Create(options ?? DefaultOptions());
    }

    public static TreeSitterNativeProbe CreateProbe(ILogger<TreeSitterNativeProbe>? logger = null)
    {
        return new TreeSitterNativeProbe(logger ?? NullLogger<TreeSitterNativeProbe>.Instance);
    }

    public static ParserPool CreatePool(AiReviewOptions? options = null)
    {
        var o = options ?? DefaultOptions();
        return new ParserPool(o.MaxFileReviewConcurrency, o.MaxStructuralParseBytes, o.StructuralParseTimeoutMs);
    }

    public static TreeSitterStructuralCodeAnalyzer CreateAnalyzer(
        TreeSitterNativeProbe? probe = null,
        ParserPool? pool = null,
        AiReviewOptions? options = null,
        ILogger<TreeSitterStructuralCodeAnalyzer>? logger = null)
    {
        probe ??= CreateProbe();
        pool ??= CreatePool(options);
        return new TreeSitterStructuralCodeAnalyzer(
            probe,
            pool,
            WrapOptions(options),
            logger ?? NullLogger<TreeSitterStructuralCodeAnalyzer>.Instance);
    }

    public static string LoadFixture(string name)
    {
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, "Fixtures", name);
        return File.ReadAllText(path);
    }

    /// <summary>
    ///     Returns the fixture's (startLine, endLine) for the deep target function.
    ///     Computed by parsing the fixture once with a temporary analyzer.
    /// </summary>
    public static (int StartLine, int EndLine) ResolveDeepFunctionSpan(TreeSitterStructuralCodeAnalyzer analyzer, SupportedLanguage language)
    {
        var info = Fixtures[language];
        var source = LoadFixture(info.Fixture);
        var request = new StructuralParseRequest(
            info.Path,
            language,
            source,
            Array.Empty<ChangedLineRange>());
        var defs = analyzer.GetDefinitionsAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        var match = defs.FirstOrDefault(d => string.Equals(d.Name, info.DeepFunctionName, StringComparison.Ordinal));
        Assert.True(
            match is not null,
            $"Could not find definition {info.DeepFunctionName} in {info.Fixture}; available: {string.Join(", ", defs.Select(d => d.Name))}");
        return (match!.StartLine, match.EndLine);
    }
}
