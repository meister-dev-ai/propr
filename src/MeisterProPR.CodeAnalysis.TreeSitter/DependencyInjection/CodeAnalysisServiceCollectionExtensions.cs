// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Options;
using MeisterProPR.CodeAnalysis.TreeSitter.Parsing;
using MeisterProPR.CodeAnalysis.TreeSitter.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.CodeAnalysis.TreeSitter.DependencyInjection;

/// <summary>
///     Registers the internal Tree-sitter structural analyzer and its bounded native
///     parsing infrastructure.
/// </summary>
public static class CodeAnalysisServiceCollectionExtensions
{
    /// <summary>DI key under which the Tree-sitter backend is registered for the composite router.</summary>
    public const string BackendKey = "code-analysis.treesitter";

    /// <summary>
    ///     Registers the <see cref="TreeSitterStructuralCodeAnalyzer" /> backend (as a concrete
    ///     singleton, so the composite router can compose it), the <see cref="ParserPool" />, and
    ///     the startup <see cref="TreeSitterNativeProbe" />. The probe runs once at first resolution
    ///     so a missing native lib degrades to heuristic fallback. The composite is
    ///     registered as <see cref="IStructuralCodeAnalyzer" /> by the Reviewing module.
    /// </summary>
    public static IServiceCollection AddCodeAnalysisTreeSitter(this IServiceCollection services)
    {
        services.TryAddSingleton<TreeSitterNativeProbe>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<TreeSitterNativeProbe>>();
            return new TreeSitterNativeProbe(logger);
        });

        services.TryAddSingleton<ParserPool>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AiReviewOptions>>().Value;
            return new ParserPool(
                options.MaxFileReviewConcurrency,
                options.MaxStructuralParseBytes,
                options.StructuralParseTimeoutMs);
        });

        // Registered under a key so the composite router can compose this backend
        // without the concrete (internal) type leaking out of this assembly.
        services.TryAddKeyedSingleton<IStructuralCodeAnalyzer>(
            BackendKey,
            (sp, _) =>
            {
                var probe = sp.GetRequiredService<TreeSitterNativeProbe>();
                var pool = sp.GetRequiredService<ParserPool>();
                var logger = sp.GetRequiredService<ILogger<TreeSitterStructuralCodeAnalyzer>>();
                var options = sp.GetRequiredService<IOptions<AiReviewOptions>>();

                // Prime the native parse path once (background, best-effort) so the first reviewed
                // file does not pay grammar-load / JIT / native-init cost inside its parse deadline.
                if (probe.IsAvailable)
                {
                    pool.WarmUp();
                }

                return new TreeSitterStructuralCodeAnalyzer(probe, pool, options, logger);
            });

        return services;
    }
}
