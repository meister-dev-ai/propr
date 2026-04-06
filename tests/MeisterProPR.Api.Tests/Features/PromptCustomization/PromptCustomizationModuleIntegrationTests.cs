// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Auth;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.PromptCustomization.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Features.PromptCustomization;

public sealed class PromptCustomizationModuleIntegrationTests(PromptCustomizationModuleIntegrationTests.PromptCustomizationApiFactory factory)
    : IClassFixture<PromptCustomizationModuleIntegrationTests.PromptCustomizationApiFactory>
{
    [Fact]
    public async Task CreateOverride_ThenListOverrides_ReturnsPersistedOverride()
    {
        await factory.ResetStateAsync();
        var client = factory.CreateClient();

        using var createRequest = factory.CreateAuthorizedRequest(
            HttpMethod.Post,
            $"/clients/{factory.ClientId}/prompt-overrides",
            new
            {
                scope = "clientScope",
                promptKey = "SystemPrompt",
                overrideText = "Use the customized system prompt.",
            });

        var createResponse = await client.SendAsync(createRequest);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var createdBody = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("SystemPrompt", createdBody.GetProperty("promptKey").GetString());
        Assert.Equal("Use the customized system prompt.", createdBody.GetProperty("overrideText").GetString());

        using var listRequest = factory.CreateAuthorizedRequest(HttpMethod.Get, $"/clients/{factory.ClientId}/prompt-overrides");
        var listResponse = await client.SendAsync(listRequest);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listBody = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync()).RootElement;
        var overrides = listBody.EnumerateArray().ToList();
        Assert.Single(overrides);
        Assert.Equal("SystemPrompt", overrides[0].GetProperty("promptKey").GetString());
    }

    [Fact]
    public async Task UpdateOverride_WhenOverrideExists_ReturnsUpdatedText()
    {
        await factory.ResetStateAsync();
        var client = factory.CreateClient();
        var overrideId = await factory.InsertOverrideAsync("AgenticLoopGuidance", "Old instructions");

        using var updateRequest = factory.CreateAuthorizedRequest(
            HttpMethod.Put,
            $"/clients/{factory.ClientId}/prompt-overrides/{overrideId}",
            new
            {
                overrideText = "New instructions",
            });

        var updateResponse = await client.SendAsync(updateRequest);

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var body = JsonDocument.Parse(await updateResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("New instructions", body.GetProperty("overrideText").GetString());

        var persisted = await factory.GetOverrideAsync(overrideId);
        Assert.NotNull(persisted);
        Assert.Equal("New instructions", persisted!.OverrideText);
    }

    [Fact]
    public async Task DeleteOverride_WhenOverrideExists_RemovesPersistedOverride()
    {
        await factory.ResetStateAsync();
        var client = factory.CreateClient();
        var overrideId = await factory.InsertOverrideAsync("SynthesisSystemPrompt", "Delete me");

        using var deleteRequest = factory.CreateAuthorizedRequest(
            HttpMethod.Delete,
            $"/clients/{factory.ClientId}/prompt-overrides/{overrideId}");

        var deleteResponse = await client.SendAsync(deleteRequest);

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        Assert.Null(await factory.GetOverrideAsync(overrideId));
    }

    public sealed class PromptCustomizationApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-prompt-customization-jwt-secret!";
        private readonly string _dbName = $"TestDb_PromptCustomization_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        public Guid ClientId { get; } = Guid.NewGuid();

        public Guid ClientAdministratorUserId { get; } = Guid.NewGuid();

        public HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string uri, object? body = null)
        {
            var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", this.GenerateUserToken(this.ClientAdministratorUserId));

            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            return request;
        }

        public async Task ResetStateAsync()
        {
            using var scope = this.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.PromptOverrides.RemoveRange(db.PromptOverrides);
            await db.SaveChangesAsync();
        }

        public async Task<Guid> InsertOverrideAsync(string promptKey, string overrideText)
        {
            var id = Guid.NewGuid();

            using var scope = this.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.PromptOverrides.Add(new PromptOverrideRecord
            {
                Id = id,
                ClientId = this.ClientId,
                Scope = PromptOverrideScope.ClientScope,
                PromptKey = promptKey,
                OverrideText = overrideText,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            });
            await db.SaveChangesAsync();

            return id;
        }

        public async Task<PromptOverrideRecord?> GetOverrideAsync(Guid id)
        {
            using var scope = this.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            return await db.PromptOverrides.AsNoTracking().FirstOrDefaultAsync(record => record.Id == id);
        }

        public string GenerateUserToken(Guid userId)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity([
                    new Claim("sub", userId.ToString()),
                    new Claim("global_role", AppUserRole.User.ToString()),
                ]),
                Expires = DateTime.UtcNow.AddHours(1),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
                Issuer = "meisterpropr",
                Audience = "meisterpropr",
            };

            return handler.WriteToken(handler.CreateToken(descriptor));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("ADO_SKIP_TOKEN_VALIDATION", "true");
            builder.UseSetting("ADO_STUB_PR", "true");
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;
            var clientId = this.ClientId;
            var adminUserId = this.ClientAdministratorUserId;

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();

                services.AddSingleton<IJwtTokenService, JwtTokenService>();
                services.AddDbContext<MeisterProPRDbContext>(options => options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddDbContextFactory<MeisterProPRDbContext>(options => options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddScoped<IPromptOverrideRepository, PromptOverrideRepository>();

                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPrFetcher>());
                services.AddSingleton(Substitute.For<IClientRegistry>());
                services.AddSingleton(Substitute.For<IClientAdoOrganizationScopeRepository>());
                services.AddSingleton(Substitute.For<IJobRepository>());

                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                userRepo.GetUserClientRolesAsync(adminUserId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole> { [clientId] = ClientRole.ClientAdministrator }));
                userRepo.GetUserClientRolesAsync(Arg.Is<Guid>(value => value != adminUserId), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                services.AddSingleton(userRepo);

                var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
                crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));
                services.AddSingleton(crawlRepo);

                var adoCredentialRepository = Substitute.For<IClientAdoCredentialRepository>();
                adoCredentialRepository.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<ClientAdoCredentials?>(null));
                services.AddSingleton(adoCredentialRepository);
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Database.EnsureCreated();

            if (!db.Clients.Any(record => record.Id == this.ClientId))
            {
                db.Clients.Add(new ClientRecord
                {
                    Id = this.ClientId,
                    DisplayName = "Prompt Customization Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                db.SaveChanges();
            }

            return host;
        }
    }
}
