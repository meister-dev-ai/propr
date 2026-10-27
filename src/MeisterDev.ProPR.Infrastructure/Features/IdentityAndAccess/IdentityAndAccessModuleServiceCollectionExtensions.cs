// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.TenantData;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Features.IdentityAndAccess.Authentication.Ports;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Authentication.Persistence;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.TenantData;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;

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
        services.AddScoped<ITenantDataLifecycle, TenantDataLifecycle>();
        services.AddScoped<ITenantMachineCredentialStore, EfTenantMachineCredentialStore>();
        services.AddScoped<ExternalSignInRedemptions>();

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

        // The identity provider for platform administrators. Its settings are checked at startup in every host, so
        // a wrong value stops the start, not the first sign-in. Plain http is accepted in Development only.
        var isDevelopment = environment?.IsDevelopment() ?? false;
        services.AddOptions<PlatformAdminSsoOptions>()
            .Configure(options =>
            {
                options.Authority = ReadSetting(configuration, PlatformAdminSsoOptions.AuthorityKey);
                options.MetadataUrl = ReadSetting(configuration, PlatformAdminSsoOptions.MetadataUrlKey);
                options.ClientId = ReadSetting(configuration, PlatformAdminSsoOptions.ClientIdKey);
                options.ClientSecret = ReadSetting(configuration, PlatformAdminSsoOptions.ClientSecretKey);
                options.RoleClaim = ReadSetting(configuration, PlatformAdminSsoOptions.RoleClaimKey);
                options.RequiredRole = ReadSetting(configuration, PlatformAdminSsoOptions.RequiredRoleKey);
                options.DisplayName = ReadSetting(configuration, PlatformAdminSsoOptions.DisplayNameKey);
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PlatformAdminSsoOptions>>(new PlatformAdminSsoOptionsValidator(isDevelopment));

        // Every request to an identity provider goes through this client: discovery, keys, token requests and the
        // user lookups of GitHub. Outside Development it uses https only, because a token request carries the client
        // secret, the code and the code verifier. An answer above the buffer limit fails the request.
        services.AddHttpClient(AuthorizationCodeFlow.HttpClientName)
            .ConfigureHttpClient(client => client.MaxResponseContentBufferSize = AuthorizationCodeFlow.MaxResponseContentBytes)
            .AddHttpMessageHandler(serviceProvider => new ExternalSignInTransportHandler(
                isDevelopment,
                serviceProvider.GetRequiredService<ILogger<ExternalSignInTransportHandler>>()));

        if (configuration.HasDatabaseConnectionString())
        {
            services.AddScoped<IUserRepository, AppUserRepository>();
            services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
            services.AddScoped<IUserPatRepository, UserPatRepository>();
            services.AddScoped<ITenantMembershipService, TenantMembershipService>();
            services.AddScoped<ITenantMemberClientAccessService, TenantMemberClientAccessService>();
            services.AddScoped<ITenantSsoProviderService, TenantSsoProviderService>();

            // OIDC id_token validation resolves and caches each provider's discovery document + JWKS per
            // metadata address, so it is a singleton. The client named AuthorizationCodeFlow.HttpClientName carries
            // the fetch.
            services.AddSingleton<IOidcTokenValidator>(serviceProvider =>
            {
                var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
                var validatorLogger = serviceProvider.GetRequiredService<ILogger<OidcTokenValidator>>();
                return new OidcTokenValidator(
                    metadataAddress => new ConfigurationManager<OpenIdConnectConfiguration>(
                        metadataAddress,
                        new OpenIdConnectConfigurationRetriever(),
                        new HttpDocumentRetriever(httpClientFactory.CreateClient(AuthorizationCodeFlow.HttpClientName))
                        {
                            RequireHttps = !isDevelopment,
                        }),
                    validatorLogger,
                    allowPlainHttpEndpoints: isDevelopment);
            });
            services.AddScoped<ITenantAuthService, TenantAuthService>();
            services.AddScoped<IPlatformAdminSignInService, PlatformAdminSignInService>();
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

    /// <summary>Returns the trimmed value of a setting, or <see langword="null" /> when it is missing or blank.</summary>
    private static string? ReadSetting(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
