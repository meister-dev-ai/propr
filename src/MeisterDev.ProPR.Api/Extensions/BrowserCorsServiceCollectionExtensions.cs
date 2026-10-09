// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using Microsoft.AspNetCore.Cors.Infrastructure;

namespace MeisterDev.ProPR.Api.Extensions;

internal static class BrowserCorsServiceCollectionExtensions
{
    public static IServiceCollection AddBrowserCorsPolicies(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCors();
        services.AddOptions<CorsOptions>().Configure<IEnumerable<ScmBrowserOriginDeclaration>>((options, declarations) =>
        {
            var localDeclarations = declarations.ToArray();
            var allowedOrigins = BrowserOriginPolicy.GetAllowedOrigins(configuration, localDeclarations);
            options.AddDefaultPolicy(policy => policy
                .WithOrigins(allowedOrigins)
                // Native declarations include host suffixes that require a predicate match.
                .SetIsOriginAllowed(origin => BrowserOriginPolicy.IsAllowedOrigin(origin, allowedOrigins, localDeclarations))
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials());
        });
        return services;
    }
}
