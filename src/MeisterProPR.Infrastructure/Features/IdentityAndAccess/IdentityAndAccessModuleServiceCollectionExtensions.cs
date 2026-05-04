// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Auth;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess.Persistence;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeisterProPR.Infrastructure.Features.IdentityAndAccess;

/// <summary>
///     Extension methods for registering the Identity and Access module.
/// </summary>
public static class IdentityAndAccessModuleServiceCollectionExtensions
{
    /// <summary>
    ///     Registers auth, credential bootstrap, and persistence services for user identity flows.
    /// </summary>
    public static IServiceCollection AddIdentityAndAccessModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        // Tenant administration is EF-backed and must be available in test hosts that swap in
        // an in-memory DbContext without providing a full DB connection string.
        services.AddScoped<ITenantAdminService, TenantAdminService>();

        if (configuration.HasDatabaseConnectionString())
        {
            services.AddScoped<IUserRepository, AppUserRepository>();
            services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
            services.AddScoped<IUserPatRepository, UserPatRepository>();
            services.AddScoped<ITenantMembershipService, TenantMembershipService>();
            services.AddScoped<ITenantSsoProviderService, TenantSsoProviderService>();
            services.AddScoped<ITenantAuthService, TenantAuthService>();
            services.AddScoped<ISessionFactory, SessionFactory>();
            services.AddScoped<SecretBackfillService>();
            services.AddTransient<SystemTenantBootstrapService>();
            services.AddTransient<AdminBootstrapService>();
        }

        services.AddSingleton<IPasswordHashService, PasswordHashService>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();

        return services;
    }
}
