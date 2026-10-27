// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net.Http.Headers;
using MeisterDev.ProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterDev.ProPR.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

/// <summary>
///     The host of <see cref="TenantAdministrationApiFactory" /> with an identity provider for provisioned users
///     configured. The provider's discovery document is the static one of the base factory, and its token endpoint
///     answers from the queue of external sign-in responses. The review jobs and their protocols are stored in the
///     in-memory database of the host, so a test reads them through the endpoints as the caller it names.
/// </summary>
public sealed class ProvisionedUserApiFactory : TenantAdministrationApiFactory
{
    public const string Authority = "https://id.example.test/realms/customers";
    public const string AuthorizationEndpoint = Authority + "/protocol/openid-connect/auth";
    public const string TokenEndpoint = Authority + "/protocol/openid-connect/token";
    public const string ClientId = "propr-cell";
    public const string ClientSecret = "cell-client-secret";
    public const string DisplayName = "Meister DEV";

    /// <summary>The audit log of the host, which the tests read the recorded events from.</summary>
    public IUserAccountAuditLog AuditLog { get; } = Substitute.For<IUserAccountAuditLog>();

    /// <summary>A client that keeps cookies and does not follow redirects, as a browser on the sign-in pages.</summary>
    public HttpClient CreateBrowser()
    {
        return this.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    }

    /// <summary>A client that sends the access token of a session.</summary>
    public HttpClient CreateClientWithAccessToken(string accessToken)
    {
        var client = this.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>A client that sends a personal access token, as the browser extension does.</summary>
    public HttpClient CreateClientWithPersonalAccessToken(string personalAccessToken)
    {
        var client = this.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Pat", personalAccessToken);
        return client;
    }

    /// <summary>Seeds a completed review job of the client with one protocol pass, the execution trace of the review.</summary>
    public async Task<Guid> SeedReviewWithTraceAsync(Guid clientId, string summary)
    {
        using var scope = this.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), clientId, "https://dev.azure.com/org", "project", "repository", Random.Shared.Next(1, 100000), 1);
        job.Protocols.Add(
            new ReviewJobProtocol
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                AttemptNumber = 1,
                Label = "src/Program.cs",
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CompletedAt = DateTimeOffset.UtcNow,
                Outcome = "Completed",
            });
        await jobs.AddAsync(job);
        await jobs.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);
        await jobs.SetResultAsync(job.Id, new ReviewResult(summary, []));
        return job.Id;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseSetting(ProvisionedUserSsoOptions.AuthorityKey, Authority);
        builder.UseSetting(ProvisionedUserSsoOptions.ClientIdKey, ClientId);
        builder.UseSetting(ProvisionedUserSsoOptions.ClientSecretKey, ClientSecret);
        builder.UseSetting(ProvisionedUserSsoOptions.DisplayNameKey, DisplayName);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IUserAccountAuditLog>();
            services.AddSingleton(this.AuditLog);
            services.AddScoped<IProvisionedUserSignInService, ProvisionedUserSignInService>();
            RowByRowRevocations.Register(services);

            services.RemoveAll<IDbContextFactory<MeisterProPRDbContext>>();
            services.AddScoped<IDbContextFactory<MeisterProPRDbContext>>(serviceProvider =>
                new HostDatabaseContexts(serviceProvider.GetRequiredService<DbContextOptions<MeisterProPRDbContext>>()));
            services.RemoveAll<IJobRepository>();
            services.AddScoped<IJobRepository, JobRepository>();
            services.RemoveAll<IReviewDiagnosticsReader>();
            services.AddScoped<IReviewDiagnosticsReader>(serviceProvider => new EfReviewDiagnosticsReader(
                serviceProvider.GetRequiredService<IJobRepository>(),
                serviceProvider.GetRequiredService<IDbContextFactory<MeisterProPRDbContext>>()));
        });
    }

    /// <summary>Creates contexts on the in-memory database of the host, which the job repository asks for.</summary>
    private sealed class HostDatabaseContexts(DbContextOptions<MeisterProPRDbContext> options) : IDbContextFactory<MeisterProPRDbContext>
    {
        public MeisterProPRDbContext CreateDbContext()
        {
            return new MeisterProPRDbContext(options);
        }
    }
}
