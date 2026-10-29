// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

/// <summary>
///     The review limits of a tenant, read and set through the API the way the hosting platform does: by a platform
///     administrator, with a bearer token or with a personal access token in <c>X-User-Pat</c>.
/// </summary>
public sealed class TenantReviewLimitsApiTests(TenantAdministrationApiFactory factory) : IClassFixture<TenantAdministrationApiFactory>
{
    [Fact]
    public async Task APlatformAdministrator_SetsBothLimitsAndReadsThemBack()
    {
        factory.ResetLicensing();
        var tenantId = await factory.SeedTenantAsync($"limits-{Guid.NewGuid():N}", "Limited");
        using var client = this.PlatformAdministrator();

        var put = await client.PutAsJsonAsync(Path(tenantId), new { maxConcurrentReviews = 2, maxReviewDurationSeconds = 1800 });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        AssertLimits(await ReadJsonAsync(put), 2, 1800);
        var get = await client.GetAsync(Path(tenantId));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        AssertLimits(await ReadJsonAsync(get), 2, 1800);

        // Everyone who may read the tenant sees the limits on the tenant itself.
        var tenant = await ReadJsonAsync(await client.GetAsync($"/admin/tenants/{tenantId}"));
        AssertLimits(tenant.GetProperty("reviewExecutionLimits"), 2, 1800);
    }

    [Fact]
    public async Task ATenantWithoutLimits_ReadsAsTwoNulls()
    {
        var tenantId = await factory.SeedTenantAsync($"limits-{Guid.NewGuid():N}", "Unlimited");
        using var client = this.PlatformAdministrator();

        var get = await client.GetAsync(Path(tenantId));

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        AssertLimits(await ReadJsonAsync(get), null, null);
    }

    // The values are stored as a whole, so a null value or a value left out clears that limit.
    [Fact]
    public async Task NullValues_ClearTheLimits()
    {
        var tenantId = await factory.SeedTenantAsync($"limits-{Guid.NewGuid():N}", "Limited");
        using var client = this.PlatformAdministrator();
        await client.PutAsJsonAsync(Path(tenantId), new { maxConcurrentReviews = 3, maxReviewDurationSeconds = 600 });

        var cleared = await client.PutAsJsonAsync(Path(tenantId), new { maxConcurrentReviews = (int?)null });

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        AssertLimits(await ReadJsonAsync(cleared), null, null);
        AssertLimits(await ReadJsonAsync(await client.GetAsync(Path(tenantId))), null, null);
    }

    // A platform that sends the same values on every synchronization gets the same answer each time, and the audit
    // history of the tenant records the change once.
    [Fact]
    public async Task SettingTheSameLimitsAgain_ChangesNothingAndRecordsOneChange()
    {
        var tenantId = await factory.SeedTenantAsync($"limits-{Guid.NewGuid():N}", "Limited");
        var adminId = await factory.SeedUserAsync($"platform.{Guid.NewGuid():N}", $"{Guid.NewGuid():N}@example.test", AppUserRole.Admin);
        using var client = this.PlatformAdministrator(adminId);

        var first = await client.PutAsJsonAsync(Path(tenantId), new { maxConcurrentReviews = 1, maxReviewDurationSeconds = 900 });
        var second = await client.PutAsJsonAsync(Path(tenantId), new { maxConcurrentReviews = 1, maxReviewDurationSeconds = 900 });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        AssertLimits(await ReadJsonAsync(second), 1, 900);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var entry = Assert.Single(
            await database.TenantAuditEntries
                .Where(audit => audit.TenantId == tenantId && audit.EventType == TenantReviewExecutionLimitsStore.AuditEventType)
                .ToListAsync());
        Assert.Equal(adminId, entry.ActorUserId);
        Assert.Contains("maxConcurrentReviews=1 (was no limit)", entry.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 60)]
    [InlineData(500, 86_400)]
    public async Task TheBoundsOfEachRange_AreAccepted(int maxConcurrentReviews, int maxReviewDurationSeconds)
    {
        var tenantId = await factory.SeedTenantAsync($"limits-{Guid.NewGuid():N}", "Limited");
        using var client = this.PlatformAdministrator();

        var put = await client.PutAsJsonAsync(Path(tenantId), new { maxConcurrentReviews, maxReviewDurationSeconds });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        AssertLimits(await ReadJsonAsync(put), maxConcurrentReviews, maxReviewDurationSeconds);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(null, 59)]
    [InlineData(null, 86_401)]
    [InlineData(null, 0)]
    public async Task AValueOutsideItsRange_IsRefusedWith400AndStoresNothing(int? maxConcurrentReviews, int? maxReviewDurationSeconds)
    {
        var tenantId = await factory.SeedTenantAsync($"limits-{Guid.NewGuid():N}", "Limited");
        using var client = this.PlatformAdministrator();
        await client.PutAsJsonAsync(Path(tenantId), new { maxConcurrentReviews = 4, maxReviewDurationSeconds = 1200 });

        var put = await client.PutAsJsonAsync(Path(tenantId), new { maxConcurrentReviews, maxReviewDurationSeconds });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.True((await ReadJsonAsync(put)).TryGetProperty("errors", out _));
        AssertLimits(await ReadJsonAsync(await client.GetAsync(Path(tenantId))), 4, 1200);
    }

    [Fact]
    public async Task AnUnknownTenant_IsAnswered404()
    {
        using var client = this.PlatformAdministrator();
        var unknown = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Path(unknown))).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync(Path(unknown), new { maxConcurrentReviews = 1, maxReviewDurationSeconds = 600 })).StatusCode);
    }

    // The limits bound what a tenant may run, so the tenant's own administrators read them on the tenant and do not
    // set them.
    [Fact]
    public async Task ATenantAdministrator_IsRefused403()
    {
        factory.ResetLicensing();
        var tenantId = await factory.SeedTenantAsync($"limits-{Guid.NewGuid():N}", "Limited");
        var userId = await factory.SeedUserAsync($"tenant.admin.{Guid.NewGuid():N}", $"{Guid.NewGuid():N}@example.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantAdministrator);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateToken(userId, AppUserRole.User));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Path(tenantId))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync(Path(tenantId), new { maxConcurrentReviews = 1, maxReviewDurationSeconds = 600 })).StatusCode);
        AssertLimits((await ReadJsonAsync(await client.GetAsync($"/admin/tenants/{tenantId}"))).GetProperty("reviewExecutionLimits"), null, null);
    }

    [Fact]
    public async Task ACallerWithoutCredentials_IsRefused401()
    {
        var tenantId = await factory.SeedTenantAsync($"limits-{Guid.NewGuid():N}", "Limited");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Path(tenantId))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PutAsJsonAsync(Path(tenantId), new { maxConcurrentReviews = 1, maxReviewDurationSeconds = 600 })).StatusCode);
    }

    // The hosting platform calls the endpoint with the personal access token of a platform administrator.
    [Fact]
    public async Task APersonalAccessTokenOfAPlatformAdministrator_SetsTheLimits()
    {
        factory.ResetLicensing();
        var tenantId = await factory.SeedTenantAsync($"limits-{Guid.NewGuid():N}", "Limited");
        var adminId = await factory.SeedUserAsync($"platform.{Guid.NewGuid():N}", $"{Guid.NewGuid():N}@example.test", AppUserRole.Admin);
        using var creation = new HttpRequestMessage(HttpMethod.Post, "/users/me/pats")
        {
            Content = JsonContent.Create(new { label = "hosting platform" }),
        };
        creation.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateToken(adminId, AppUserRole.Admin));
        var token = (await ReadJsonAsync(await factory.CreateClient().SendAsync(creation))).GetProperty("token").GetString();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Pat", token);

        var put = await client.PutAsJsonAsync(Path(tenantId), new { maxConcurrentReviews = 3, maxReviewDurationSeconds = 3600 });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        AssertLimits(await ReadJsonAsync(await client.GetAsync(Path(tenantId))), 3, 3600);
    }

    private static string Path(Guid tenantId)
    {
        return $"/admin/tenants/{tenantId}/review-limits";
    }

    private static void AssertLimits(JsonElement body, int? maxConcurrentReviews, int? maxReviewDurationSeconds)
    {
        AssertValue(body.GetProperty("maxConcurrentReviews"), maxConcurrentReviews);
        AssertValue(body.GetProperty("maxReviewDurationSeconds"), maxReviewDurationSeconds);

        static void AssertValue(JsonElement value, int? expected)
        {
            if (expected is null)
            {
                Assert.Equal(JsonValueKind.Null, value.ValueKind);
            }
            else
            {
                Assert.Equal(expected, value.GetInt32());
            }
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private HttpClient PlatformAdministrator(Guid? userId = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(userId ?? Guid.NewGuid(), AppUserRole.Admin));
        return client;
    }
}
