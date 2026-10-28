// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

public sealed class ProvisionedUserPatApiTests
{
    [Theory]
    [InlineData(AppUserRole.User)]
    [InlineData(AppUserRole.Admin)]
    public async Task CreatePat_WithAnExpiredDate_ReturnsBadRequestWithoutStoringAToken(AppUserRole role)
    {
        await using var factory = new LimitedPatApiFactory();
        var userId = await SeedProvisionedUserAsync(factory, role);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateToken(userId, role));

        using var response = await client.PostAsJsonAsync("/users/me/pats", new { label = "Expired token", expiresAt = DateTimeOffset.UtcNow });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        Assert.Empty(await db.UserPats.Where(pat => pat.UserId == userId).ToListAsync());
    }

    [Theory]
    [InlineData(null, 90)]
    [InlineData(120, 90)]
    [InlineData(30, 30)]
    public async Task CreatePat_ReturnsTheExpiryAppliedToTheCustomerToken(int? requestedDays, int expectedDays)
    {
        await using var factory = new LimitedPatApiFactory();
        var userId = await SeedProvisionedUserAsync(factory, AppUserRole.User);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateToken(userId, AppUserRole.User));
        var requestedExpiry = requestedDays.HasValue ? DateTimeOffset.UtcNow.AddDays(requestedDays.Value) : (DateTimeOffset?)null;

        using var response = await client.PostAsJsonAsync("/users/me/pats", new { label = "Browser extension", expiresAt = requestedExpiry });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var createdAt = body.RootElement.GetProperty("createdAt").GetDateTimeOffset();
        var expiresAt = body.RootElement.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.Equal(expectedDays == 30 ? requestedExpiry : createdAt.AddDays(90), expiresAt);
        using var list = JsonDocument.Parse(await client.GetStringAsync("/users/me/pats"));
        Assert.Equal(expiresAt, Assert.Single(list.RootElement.EnumerateArray()).GetProperty("expiresAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData(AppUserRole.User, 90)]
    [InlineData(AppUserRole.Admin, null)]
    public async Task GetPolicy_ReturnsTheCurrentUsersLimitAndExemptsPlatformAdministrators(AppUserRole role, int? expectedLimit)
    {
        await using var factory = new LimitedPatApiFactory();
        var userId = await SeedProvisionedUserAsync(factory, role);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateToken(userId, role));

        using var response = await client.GetAsync("/users/me/pats/policy");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var limit = body.RootElement.GetProperty("maximumLifetimeDays");
        Assert.Equal(expectedLimit, limit.ValueKind == JsonValueKind.Null ? null : limit.GetInt32());
    }

    [Fact]
    public async Task GetPolicy_WithoutAnAuthenticatedUser_ReturnsUnauthorized()
    {
        await using var factory = new LimitedPatApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/users/me/pats/policy");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<Guid> SeedProvisionedUserAsync(LimitedPatApiFactory factory, AppUserRole role)
    {
        var userId = await factory.SeedUserAsync($"person-{Guid.NewGuid():N}", "person@example.test", role);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        db.ProvisionedUsers.Add(
            new ProvisionedUserRecord
            {
                UserId = userId,
                Issuer = "https://identity.example.test",
                Subject = userId.ToString(),
                Email = "person@example.test",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();
        return userId;
    }

    private sealed class LimitedPatApiFactory : TenantAdministrationApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("MEISTER_PROVISIONED_USER_PAT_MAX_DAYS", "90");
        }
    }
}
