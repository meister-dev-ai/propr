// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace MeisterProPR.Infrastructure.Data;

/// <summary>EF Core database context for MeisterProPR.</summary>
public sealed class MeisterProPRDbContext(DbContextOptions<MeisterProPRDbContext> options) : DbContext(options)
{
    /// <summary>Registered clients table.</summary>
    public DbSet<ClientRecord> Clients => this.Set<ClientRecord>();

    /// <summary>Tenant boundaries for sign-in policy and tenant administration.</summary>
    public DbSet<TenantRecord> Tenants => this.Set<TenantRecord>();

    /// <summary>Crawl configurations table.</summary>
    public DbSet<CrawlConfigurationRecord> CrawlConfigurations => this.Set<CrawlConfigurationRecord>();

    /// <summary>Client-scoped SCM provider connections.</summary>
    public DbSet<ClientScmConnectionRecord> ClientScmConnections => this.Set<ClientScmConnectionRecord>();

    /// <summary>Client-scoped provider scope selections.</summary>
    public DbSet<ClientScmScopeRecord> ClientScmScopes => this.Set<ClientScmScopeRecord>();

    /// <summary>Configured provider reviewer identities.</summary>
    public DbSet<ClientReviewerIdentityRecord> ClientReviewerIdentities => this.Set<ClientReviewerIdentityRecord>();

    /// <summary>Review jobs table.</summary>
    public DbSet<ReviewJob> ReviewJobs => this.Set<ReviewJob>();

    /// <summary>Per-file results of a review job.</summary>
    public DbSet<ReviewFileResult> ReviewFileResults => this.Set<ReviewFileResult>();

    /// <summary>Mention reply jobs table.</summary>
    public DbSet<MentionReplyJob> MentionReplyJobs => this.Set<MentionReplyJob>();

    /// <summary>Mention project scan watermarks table.</summary>
    public DbSet<MentionProjectScan> MentionProjectScans => this.Set<MentionProjectScan>();

    /// <summary>Mention per-PR scan watermarks table.</summary>
    public DbSet<MentionPrScan> MentionPrScans => this.Set<MentionPrScan>();

    /// <summary>Review PR scan watermarks table (one row per client+repository+PR).</summary>
    public DbSet<ReviewPrScan> ReviewPrScans => this.Set<ReviewPrScan>();

    /// <summary>Per-thread reply watermarks within a review PR scan.</summary>
    public DbSet<ReviewPrScanThread> ReviewPrScanThreads => this.Set<ReviewPrScanThread>();

    /// <summary>Review job protocol records (one per job attempt).</summary>
    public DbSet<ReviewJobProtocol> ReviewJobProtocols => this.Set<ReviewJobProtocol>();

    /// <summary>Individual step events within a review job protocol.</summary>
    public DbSet<ProtocolEvent> ProtocolEvents => this.Set<ProtocolEvent>();

    /// <summary>Application users.</summary>
    public DbSet<AppUserRecord> AppUsers => this.Set<AppUserRecord>();

    /// <summary>Tenant-scoped user memberships.</summary>
    public DbSet<TenantMembershipRecord> TenantMemberships => this.Set<TenantMembershipRecord>();

    /// <summary>Tenant-owned external sign-in provider configurations.</summary>
    public DbSet<TenantSsoProviderRecord> TenantSsoProviders => this.Set<TenantSsoProviderRecord>();

    /// <summary>Tenant-scoped external identity links.</summary>
    public DbSet<ExternalIdentityRecord> ExternalIdentities => this.Set<ExternalIdentityRecord>();

    /// <summary>Tenant administration audit history.</summary>
    public DbSet<TenantAuditEntryRecord> TenantAuditEntries => this.Set<TenantAuditEntryRecord>();

    /// <summary>Per-client role assignments for users.</summary>
    public DbSet<UserClientRoleRecord> UserClientRoles => this.Set<UserClientRoleRecord>();

    /// <summary>User-generated Personal Access Tokens.</summary>
    public DbSet<UserPatRecord> UserPats => this.Set<UserPatRecord>();

    /// <summary>Server-persisted refresh tokens.</summary>
    public DbSet<RefreshTokenRecord> RefreshTokens => this.Set<RefreshTokenRecord>();

    /// <summary>Per-client AI connection configurations.</summary>
    public DbSet<AiConnectionRecord> AiConnections => this.Set<AiConnectionRecord>();

    /// <summary>Per-deployment embedding capability metadata under one AI connection.</summary>
    public DbSet<AiConnectionModelCapabilityRecord> AiConnectionModelCapabilities =>
        this.Set<AiConnectionModelCapabilityRecord>();

    /// <summary>Provider-neutral AI connection profiles.</summary>
    public DbSet<AiConnectionProfileRecord> AiConnectionProfiles => this.Set<AiConnectionProfileRecord>();

    /// <summary>Configured models under provider-neutral AI connection profiles.</summary>
    public DbSet<AiConfiguredModelRecord> AiConfiguredModels => this.Set<AiConfiguredModelRecord>();

    /// <summary>AI purpose bindings under provider-neutral AI connection profiles.</summary>
    public DbSet<AiPurposeBindingRecord> AiPurposeBindings => this.Set<AiPurposeBindingRecord>();

    /// <summary>Latest verification snapshots for provider-neutral AI connection profiles.</summary>
    public DbSet<AiVerificationSnapshotRecord> AiVerificationSnapshots => this.Set<AiVerificationSnapshotRecord>();

    /// <summary>Repository-scope filters for crawl configurations.</summary>
    public DbSet<CrawlRepoFilterRecord> CrawlRepoFilters => this.Set<CrawlRepoFilterRecord>();

    /// <summary>Webhook configurations table.</summary>
    public DbSet<WebhookConfigurationRecord> WebhookConfigurations => this.Set<WebhookConfigurationRecord>();

    /// <summary>Repository-scope filters for webhook configurations.</summary>
    public DbSet<WebhookRepoFilterRecord> WebhookRepoFilters => this.Set<WebhookRepoFilterRecord>();

    /// <summary>Durable webhook delivery-history entries.</summary>
    public DbSet<WebhookDeliveryLogEntryRecord> WebhookDeliveryLogEntries => this.Set<WebhookDeliveryLogEntryRecord>();

    /// <summary>Append-only provider-connection operational audit entries.</summary>
    public DbSet<ProviderConnectionAuditEntryRecord> ProviderConnectionAuditEntries =>
        this.Set<ProviderConnectionAuditEntryRecord>();

    /// <summary>Installation-wide provider-family activation policy.</summary>
    public DbSet<ProviderActivationRecord> ProviderActivations => this.Set<ProviderActivationRecord>();

    /// <summary>Singleton installation edition row for Community or Commercial operation.</summary>
    public DbSet<InstallationEditionRecord> InstallationEditions => this.Set<InstallationEditionRecord>();

    /// <summary>Installation-wide override rows for premium capability state.</summary>
    public DbSet<PremiumCapabilityOverrideRecord> PremiumCapabilityOverrides => this.Set<PremiumCapabilityOverrideRecord>();

    /// <summary>Explicit ProCursor source associations for crawl configurations.</summary>
    public DbSet<CrawlConfigurationProCursorSourceRecord> CrawlConfigurationProCursorSources =>
        this.Set<CrawlConfigurationProCursorSourceRecord>();

    /// <summary>Snapshotted ProCursor source scope for queued review jobs.</summary>
    public DbSet<ReviewJobProCursorSourceScopeRecord> ReviewJobProCursorSourceScopes =>
        this.Set<ReviewJobProCursorSourceScopeRecord>();

    /// <summary>Per-client and per-crawl-config AI prompt overrides.</summary>
    public DbSet<PromptOverrideRecord> PromptOverrides => this.Set<PromptOverrideRecord>();

    /// <summary>Per-client thread memory records for semantic embedding search.</summary>
    public DbSet<ThreadMemoryRecord> ThreadMemoryRecords => this.Set<ThreadMemoryRecord>();

    /// <summary>Configured ProCursor knowledge sources.</summary>
    public DbSet<ProCursorKnowledgeSource> ProCursorKnowledgeSources => this.Set<ProCursorKnowledgeSource>();

    /// <summary>Tracked branches configured for ProCursor knowledge sources.</summary>
    public DbSet<ProCursorTrackedBranch> ProCursorTrackedBranches => this.Set<ProCursorTrackedBranch>();

    /// <summary>Durable ProCursor indexing jobs.</summary>
    public DbSet<ProCursorIndexJob> ProCursorIndexJobs => this.Set<ProCursorIndexJob>();

    /// <summary>Persisted ProCursor index snapshots.</summary>
    public DbSet<ProCursorIndexSnapshot> ProCursorIndexSnapshots => this.Set<ProCursorIndexSnapshot>();

    /// <summary>Persisted ProCursor knowledge chunks.</summary>
    public DbSet<ProCursorKnowledgeChunk> ProCursorKnowledgeChunks => this.Set<ProCursorKnowledgeChunk>();

    /// <summary>Persisted ProCursor symbol records.</summary>
    public DbSet<ProCursorSymbolRecord> ProCursorSymbolRecords => this.Set<ProCursorSymbolRecord>();

    /// <summary>Persisted ProCursor symbol edges.</summary>
    public DbSet<ProCursorSymbolEdge> ProCursorSymbolEdges => this.Set<ProCursorSymbolEdge>();

    /// <summary>Crawl-side memory lifecycle audit log (append-only).</summary>
    public DbSet<MemoryActivityLogEntry> MemoryActivityLogEntries => this.Set<MemoryActivityLogEntry>();

    /// <summary>Daily token usage aggregates per client and model.</summary>
    public DbSet<ClientTokenUsageSample> ClientTokenUsageSamples => this.Set<ClientTokenUsageSample>();

    /// <summary>Raw ProCursor token usage events.</summary>
    public DbSet<ProCursorTokenUsageEvent> ProCursorTokenUsageEvents => this.Set<ProCursorTokenUsageEvent>();

    /// <summary>Daily and monthly ProCursor token usage rollups.</summary>
    public DbSet<ProCursorTokenUsageRollup> ProCursorTokenUsageRollups => this.Set<ProCursorTokenUsageRollup>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MeisterProPRDbContext).Assembly);

        // Apply pgvector-specific configuration only when using the Npgsql provider.
        // The in-memory provider used in lightweight unit tests cannot map the Vector CLR type.
        if (this.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
        {
            modelBuilder.HasPostgresExtension("vector");
            modelBuilder.Entity<ThreadMemoryRecord>()
                .Property(r => r.EmbeddingVector)
                .HasColumnType($"vector({GetMemoryEmbeddingDimensions()})")
                .HasConversion(
                    v => new Vector(v),
                    v => v.ToArray());

            modelBuilder.Entity<ProCursorKnowledgeChunk>()
                .Property(chunk => chunk.EmbeddingVector)
                .HasColumnType($"vector({GetProCursorEmbeddingDimensions()})")
                .HasConversion(
                    v => new Vector(v),
                    v => v.ToArray());
        }
    }

    // Returns the configured embedding dimension.
    // Falls back to 1536 (the production default).
    private static int GetMemoryEmbeddingDimensions()
    {
        return 1536;
    }

    private static int GetProCursorEmbeddingDimensions()
    {
        return 1536;
    }
}
