// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using MeisterDev.ProPR.Infrastructure.Services;
using MeisterDev.ProPR.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Hosting;

/// <summary>
///     Builds the host primitives over a real database, with the connection rows they act on.
/// </summary>
/// <remarks>
///     Real protection rather than a stand-in, so that what lands in the column is what an operator would find
///     there. Every primitive opens its own context from the factory, which these fixtures have to
///     reproduce: a shared context would hide the concurrency the primitives exist to handle.
/// </remarks>
public sealed class ProviderHostPrimitiveHarness : IAsyncDisposable
{
    private readonly List<Guid> _clientIds = [];
    private readonly List<Guid> _connectionIds = [];
    private readonly List<Guid> _tenantIds = [];
    private readonly List<Guid> _userIds = [];
    private readonly string _keysDirectory;
    private readonly DbContextOptions<MeisterProPRDbContext> _options;

    private ProviderHostPrimitiveHarness(DbContextOptions<MeisterProPRDbContext> options, string keysDirectory)
    {
        this._options = options;
        this._keysDirectory = keysDirectory;
        this.Contexts = new TestDbContextFactory(options);
        this.Codec = CreateCodec(keysDirectory);
        this.Capabilities = Substitute.For<IProviderAddInCapabilityGate>();
        this.Capabilities.IsAvailableAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(true);
        this.Capabilities.IsAvailableNowAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    /// <summary>Opens a context per call, the way every primitive does.</summary>
    public IDbContextFactory<MeisterProPRDbContext> Contexts { get; }

    /// <summary>The real protection the host wraps stored values with.</summary>
    public ISecretProtectionCodec Codec { get; }

    /// <summary>The licence check, which answers yes unless a test says otherwise.</summary>
    public IProviderAddInCapabilityGate Capabilities { get; }

    /// <summary>Builds the harness over one connection string.</summary>
    /// <param name="connectionString">Where the database is.</param>
    public static ProviderHostPrimitiveHarness Create(string connectionString)
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"MeisterDev.ProPR.ProviderHostPrimitives.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        return new ProviderHostPrimitiveHarness(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
                .Options,
            keysDirectory);
    }

    /// <summary>Opens a context of the harness's own, for arranging and asserting.</summary>
    public MeisterProPRDbContext CreateContext()
    {
        return new MeisterProPRDbContext(this._options);
    }

    /// <summary>Writes one connection profile and returns what the primitives are bound to.</summary>
    /// <param name="displayName">What an operator calls it, which a refusal names.</param>
    /// <param name="addInKey">The family serving it.</param>
    /// <param name="requiredCapabilityKey">The capability the family declared, if any.</param>
    /// <param name="clientId">The owning client, or null for a connection no client owns.</param>
    /// <param name="tenantId">The owning tenant, or null for a connection no tenant owns.</param>
    /// <param name="providerSettings">The family's declared configuration values for this connection.</param>
    /// <param name="supersededIdentities">The identity spellings the family declares it supersedes.</param>
    /// <param name="ct">Cancels the write.</param>
    public async Task<ProviderAddInBinding> SeedConnectionAsync(
        string displayName,
        string addInKey = "example/provider",
        string? requiredCapabilityKey = null,
        Guid? clientId = null,
        Guid? tenantId = null,
        IReadOnlyDictionary<string, string>? providerSettings = null,
        IReadOnlyList<string>? supersededIdentities = null,
        CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var db = this.CreateContext();
        db.AiConnectionProfiles.Add(
            new AiConnectionProfileRecord
            {
                Id = id,
                ClientId = clientId,
                TenantId = tenantId,
                DisplayName = displayName,
                ProviderKind = addInKey,
                BaseUrl = "https://api.example.com/v1",
                AuthMode = addInKey + ":ApiKey",
                DiscoveryMode = "Manual",
                ProviderSettings = providerSettings?.ToDictionary(StringComparer.Ordinal),
                CreatedAt = now,
                UpdatedAt = now,
            });
        await db.SaveChangesAsync(ct);

        this._connectionIds.Add(id);

        return new ProviderAddInBinding(
            id,
            displayName,
            addInKey,
            addInKey,
            addInKey + ":ApiKey",
            requiredCapabilityKey,
            supersededIdentities);
    }

    /// <summary>Writes one account, optionally with a membership of one tenant.</summary>
    /// <param name="isPlatformAdministrator">Whether the account administers the installation.</param>
    /// <param name="tenantId">A tenant to belong to, or null for none.</param>
    /// <param name="isActive">Whether the account is enabled.</param>
    /// <param name="tenantRole">The role held in that tenant.</param>
    /// <param name="ct">Cancels the write.</param>
    public async Task<Guid> SeedAdministratorAsync(
        bool isPlatformAdministrator = false,
        Guid? tenantId = null,
        bool isActive = true,
        TenantRole tenantRole = TenantRole.TenantAdministrator,
        CancellationToken ct = default)
    {
        var id = Guid.NewGuid();

        await using var db = this.CreateContext();
        db.AppUsers.Add(
            new AppUserRecord
            {
                Id = id,
                Username = $"admin-{id:N}",
                GlobalRole = isPlatformAdministrator ? AppUserRole.Admin : AppUserRole.User,
                IsActive = isActive,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        if (tenantId is { } tenant)
        {
            db.TenantMemberships.Add(
                new TenantMembershipRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant,
                    UserId = id,
                    Role = tenantRole,
                    AssignedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
        }

        await db.SaveChangesAsync(ct);
        this._userIds.Add(id);

        return id;
    }

    /// <summary>Writes one tenant, for the connections and memberships a test needs owned by one.</summary>
    /// <param name="ct">Cancels the write.</param>
    public async Task<Guid> SeedTenantAsync(CancellationToken ct = default)
    {
        var id = Guid.NewGuid();

        await using var db = this.CreateContext();
        db.Tenants.Add(
            new TenantRecord
            {
                Id = id,
                Slug = $"t{id:N}"[..16],
                DisplayName = "Provider host primitives",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync(ct);

        this._tenantIds.Add(id);

        return id;
    }

    /// <summary>Writes one client inside a tenant, for the connections a test needs owned by one.</summary>
    /// <param name="tenantId">The tenant the client belongs to.</param>
    /// <param name="ct">Cancels the write.</param>
    public async Task<Guid> SeedClientAsync(Guid tenantId, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();

        await using var db = this.CreateContext();
        db.Clients.Add(
            new ClientRecord
            {
                Id = id,
                TenantId = tenantId,
                DisplayName = "Provider host primitives",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync(ct);

        this._clientIds.Add(id);

        return id;
    }

    /// <summary>Gives one administrator an explicit role on one client.</summary>
    /// <param name="clientId">The client the role is on.</param>
    /// <param name="adminId">The account the role is given to.</param>
    /// <param name="role">The role.</param>
    /// <param name="ct">Cancels the write.</param>
    public async Task SeedClientAssignmentAsync(
        Guid clientId,
        Guid adminId,
        ClientRole role = ClientRole.ClientAdministrator,
        CancellationToken ct = default)
    {
        await using var db = this.CreateContext();
        db.UserClientRoles.Add(
            new UserClientRoleRecord
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                UserId = adminId,
                Role = role,
                AssignedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Takes the administrator's tenant administrator membership away.</summary>
    /// <param name="adminId">The account to strip.</param>
    /// <param name="ct">Cancels the write.</param>
    public async Task RevokeTenantMembershipsAsync(Guid adminId, CancellationToken ct = default)
    {
        await using var db = this.CreateContext();
        await db.TenantMemberships.Where(membership => membership.UserId == adminId).ExecuteDeleteAsync(ct);
    }

    /// <summary>Builds the credential handle for one connection.</summary>
    /// <param name="binding">The connection it acts on.</param>
    /// <param name="timeProvider">The clock expiries are judged against.</param>
    /// <param name="lockWait">How long to wait for the row, or null for the host's own wait.</param>
    public ProviderCredentialSessions Credentials(
        ProviderAddInBinding binding,
        TimeProvider? timeProvider = null,
        TimeSpan? lockWait = null)
    {
        return new ProviderCredentialSessions(
            binding,
            this.Contexts,
            this.Codec,
            this.Capabilities,
            timeProvider ?? TimeProvider.System,
            lockWait);
    }

    /// <summary>Builds the keyed store for one connection.</summary>
    /// <param name="binding">The connection and family it is scoped to.</param>
    /// <param name="actingPrincipalId">The administrator the host recorded, if any.</param>
    /// <param name="timeProvider">The clock expiries are judged against.</param>
    public ProviderKeyedStore Store(
        ProviderAddInBinding binding,
        Guid? actingPrincipalId = null,
        TimeProvider? timeProvider = null)
    {
        return new ProviderKeyedStore(
            binding,
            actingPrincipalId,
            this.Contexts,
            this.Codec,
            timeProvider ?? TimeProvider.System);
    }

    /// <summary>Builds the lease handle for one connection.</summary>
    /// <param name="binding">The connection and family it is scoped to.</param>
    /// <param name="timeProvider">The clock expiries are judged against.</param>
    public ProviderResourceLeases Leases(ProviderAddInBinding binding, TimeProvider? timeProvider = null)
    {
        return new ProviderResourceLeases(binding, this.Contexts, timeProvider ?? TimeProvider.System);
    }

    /// <summary>Writes a credential straight into the column, standing in for what an earlier flow stored.</summary>
    /// <param name="binding">The connection to write against.</param>
    /// <param name="fields">The credential fields.</param>
    /// <param name="expiresAt">When it stops being usable, or null for a credential that states no expiry.</param>
    /// <param name="ct">Cancels the write.</param>
    public async Task SeedCredentialAsync(
        ProviderAddInBinding binding,
        IReadOnlyDictionary<string, string> fields,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        var envelope = new Ai.Providers.Contracts.ProviderSecretEnvelope(binding.AuthMode.ToString(), fields)
        {
            ExpiresAt = expiresAt,
            IdentityKey = binding.AddInKey,
        };

        var protectedSecret = this.Codec.Protect(envelope.Encode(), ProviderCredentialSessions.SecretPurpose);

        await using var db = this.CreateContext();
        await db.AiConnectionProfiles
            .Where(profile => profile.Id == binding.ConnectionProfileId)
            .ExecuteUpdateAsync(
                update => update.SetProperty(profile => profile.ProtectedSecret, protectedSecret),
                ct);
    }

    /// <summary>Removes exactly the rows this harness wrote; the database is shared across the collection.</summary>
    public async ValueTask DisposeAsync()
    {
        await using (var db = this.CreateContext())
        {
            // The entries and the leases are not reached by the connection cascade on their own, because a
            // keyed entry belongs to a family rather than to a connection.
            await db.ProviderKeyedEntries
                .Where(entry => entry.AddInKey.StartsWith("example/"))
                .ExecuteDeleteAsync();
            await db.ProviderResourceLeases
                .Where(lease => lease.AddInKey.StartsWith("example/"))
                .ExecuteDeleteAsync();
            await db.AiConnectionProfiles
                .Where(profile => this._connectionIds.Contains(profile.Id))
                .ExecuteDeleteAsync();
            await db.TenantMemberships
                .Where(membership => this._userIds.Contains(membership.UserId))
                .ExecuteDeleteAsync();
            await db.UserClientRoles
                .Where(assignment => this._userIds.Contains(assignment.UserId))
                .ExecuteDeleteAsync();
            await db.AppUsers.Where(user => this._userIds.Contains(user.Id)).ExecuteDeleteAsync();
            await db.Clients.Where(client => this._clientIds.Contains(client.Id)).ExecuteDeleteAsync();
            await db.Tenants.Where(tenant => this._tenantIds.Contains(tenant.Id)).ExecuteDeleteAsync();
        }

        try
        {
            Directory.Delete(this._keysDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A key file still open leaves a temporary directory behind and nothing else.
        }
    }

    private static ISecretProtectionCodec CreateCodec(string keysDirectory)
    {
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterDev.ProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        return new SecretProtectionCodec(services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>());
    }
}
