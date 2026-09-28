// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common.DependencyInjection;

/// <summary>
///     Registers the composer that puts the AI-generated marker on outbound comment bodies.
/// </summary>
/// <remarks>
///     Called by every provider module, because each one's publication and reply adapters take the composer.
///     Registering it with the adapters keeps a composition that holds one provider from needing the whole
///     infrastructure set. The wording itself is bound from the environment by the host that posts; without
///     that binding the default wording applies.
/// </remarks>
internal static class PostedCommentComposerServiceCollectionExtensions
{
    public static IServiceCollection AddPostedCommentComposer(this IServiceCollection services)
    {
        services.AddOptions<PostedCommentMarkerOptions>();
        services.TryAddSingleton<IPostedCommentComposer, PostedCommentComposer>();

        return services;
    }
}
