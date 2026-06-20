// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeisterProPR.CodeAnalysis.Roslyn.DependencyInjection;

/// <summary>
///     Registers the Roslyn-syntax structural analyzer backend. Registered as a
///     concrete singleton so the composite router can compose it alongside the Tree-sitter backend;
///     the composite is registered as <see cref="IStructuralCodeAnalyzer" /> by the Reviewing module.
/// </summary>
public static class CodeAnalysisRoslynServiceCollectionExtensions
{
    /// <summary>DI key under which the Roslyn-syntax backend is registered for the composite router.</summary>
    public const string BackendKey = "code-analysis.roslyn";

    /// <summary>Registers <see cref="RoslynSyntaxStructuralAnalyzer" /> as a keyed singleton backend.</summary>
    public static IServiceCollection AddCodeAnalysisRoslyn(this IServiceCollection services)
    {
        services.TryAddKeyedSingleton<IStructuralCodeAnalyzer, RoslynSyntaxStructuralAnalyzer>(BackendKey);
        return services;
    }
}
