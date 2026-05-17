// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.ProRV.Abstractions;
using MeisterProPR.ProRV.Core;
using MeisterProPR.ProRV.Knowledge;
using MeisterProPR.ProRV.Prompting;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.ProRV.DependencyInjection;

/// <summary>
///     Dependency-injection registration helpers for the ProRV library.
/// </summary>
public static class ProRVServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the bounded ProRV services and embedded knowledge assets.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddProRV(this IServiceCollection services)
    {
        services.AddSingleton<IProRVKnowledgeCatalog, EmbeddedProRVKnowledgeCatalog>();
        services.AddSingleton<ProRVPromptFactory>();
        services.AddSingleton<IProRVPrefilter>(provider =>
            new ProRVPrefilter(
                provider.GetRequiredService<IProRVKnowledgeCatalog>(),
                provider.GetRequiredService<ProRVPromptFactory>()));
        return services;
    }
}
