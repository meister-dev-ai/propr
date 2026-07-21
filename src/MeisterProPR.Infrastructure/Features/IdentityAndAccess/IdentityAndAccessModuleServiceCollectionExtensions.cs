// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Infrastructure.Auth;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess.Persistence;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

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
        // Session lifetimes are needed both by the DB-backed refresh-token repository and by the
        // auth controllers, so register the policy unconditionally.
        services.AddSingleton(SessionPolicyFactory.FromConfiguration(configuration));

        // Tenant administration is EF-backed and must be available in test hosts that swap in
        // an in-memory DbContext without providing a full DB connection string.
        services.AddScoped<ITenantAdminService, TenantAdminService>();

        services.AddOptions<AccountLockoutOptions>()
            .Configure(options =>
            {
                if (int.TryParse(configuration["MEISTER_AUTH_LOCKOUT_MAX_ATTEMPTS"], out var maxAttempts))
                {
                    options.MaxFailedAttempts = maxAttempts;
                }

                if (int.TryParse(configuration["MEISTER_AUTH_LOCKOUT_BASE_MINUTES"], out var baseMinutes))
                {
                    options.BaseLockoutMinutes = baseMinutes;
                }

                if (int.TryParse(configuration["MEISTER_AUTH_LOCKOUT_MAX_MINUTES"], out var maxMinutes))
                {
                    options.MaxLockoutMinutes = maxMinutes;
                }
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        if (configuration.HasDatabaseConnectionString())
        {
            services.AddScoped<IUserRepository, AppUserRepository>();
            services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
            services.AddScoped<IUserPatRepository, UserPatRepository>();
            services.AddScoped<ITenantMembershipService, TenantMembershipService>();
            services.AddScoped<ITenantMemberClientAccessService, TenantMemberClientAccessService>();
            services.AddScoped<ITenantSsoProviderService, TenantSsoProviderService>();

            // OIDC id_token validation resolves and caches each provider's discovery document + JWKS per
            // metadata address, so it is a singleton. The named "TenantSsoAuth" client carries the fetch.
            services.AddSingleton<ITenantOidcTokenValidator>(serviceProvider =>
            {
                var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
                var validatorLogger = serviceProvider.GetRequiredService<ILogger<TenantOidcTokenValidator>>();
                return new TenantOidcTokenValidator(
                    metadataAddress => new ConfigurationManager<OpenIdConnectConfiguration>(
                        metadataAddress,
                        new OpenIdConnectConfigurationRetriever(),
                        new HttpDocumentRetriever(httpClientFactory.CreateClient("TenantSsoAuth"))),
                    validatorLogger);
            });
            services.AddScoped<ITenantAuthService, TenantAuthService>();
            services.AddScoped<IAccountLockoutService, AccountLockoutService>();
            services.AddScoped<ISessionFactory, SessionFactory>();
            services.AddScoped<SecretBackfillService>();
            services.AddTransient<SystemTenantBootstrapService>();
            services.AddTransient<AdminBootstrapService>();
        }

        services.AddSingleton<IPasswordHashService, PasswordHashService>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<IUserAccountAuditLog, UserAccountAuditLog>();

        return services;
    }
}
