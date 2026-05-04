// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.DependencyInjection;

internal static class CommentRelevanceServiceCollectionExtensions
{
    public static IServiceCollection AddCommentRelevanceFiltering(
        this IServiceCollection services,
        string? selectedImplementationId = null)
    {
        services.AddSingleton(new CommentRelevanceFilterSelection(selectedImplementationId));
        services.AddSingleton<ICommentRelevanceAmbiguityEvaluator, AiCommentRelevanceAmbiguityEvaluator>();

        services.AddSingleton<PassThroughCommentRelevanceFilter>();
        services.AddSingleton<HeuristicCommentRelevanceFilter>();
        services.AddSingleton<HybridCommentRelevanceFilter>();
        services.AddSingleton<ICommentRelevanceFilter>(sp => sp.GetRequiredService<PassThroughCommentRelevanceFilter>());
        services.AddSingleton<ICommentRelevanceFilter>(sp => sp.GetRequiredService<HeuristicCommentRelevanceFilter>());
        services.AddSingleton<ICommentRelevanceFilter>(sp => sp.GetRequiredService<HybridCommentRelevanceFilter>());
        services.AddSingleton<CommentRelevanceFilterRegistry>();

        return services;
    }
}
