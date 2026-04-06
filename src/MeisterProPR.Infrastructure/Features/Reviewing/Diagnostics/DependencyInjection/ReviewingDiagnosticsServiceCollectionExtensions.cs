// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetReviewJobProtocol;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.DependencyInjection;

/// <summary>
///     Registers Reviewing diagnostics boundaries and read models.
/// </summary>
public static class ReviewingDiagnosticsServiceCollectionExtensions
{
    /// <summary>
    ///     Registers Reviewing diagnostics services.
    /// </summary>
    public static IServiceCollection AddReviewingDiagnostics(this IServiceCollection services)
    {
        services.AddSingleton<IReviewProtocolRecorder>(sp =>
            new LegacyReviewProtocolRecorderAdapter(sp.GetRequiredService<IProtocolRecorder>()));
        services.AddScoped<IReviewDiagnosticsReader, EfReviewDiagnosticsReader>();
        services.AddScoped<GetReviewJobProtocolHandler>();

        return services;
    }
}
