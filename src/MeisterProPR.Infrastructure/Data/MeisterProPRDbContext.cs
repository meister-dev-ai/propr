using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Data;

/// <summary>EF Core database context for MeisterProPR.</summary>
public sealed class MeisterProPRDbContext(DbContextOptions<MeisterProPRDbContext> options) : DbContext(options)
{
    /// <summary>Registered clients table.</summary>
    public DbSet<ClientRecord> Clients => this.Set<ClientRecord>();

    /// <summary>Crawl configurations table.</summary>
    public DbSet<CrawlConfigurationRecord> CrawlConfigurations => this.Set<CrawlConfigurationRecord>();

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

    /// <summary>Per-client role assignments for users.</summary>
    public DbSet<UserClientRoleRecord> UserClientRoles => this.Set<UserClientRoleRecord>();

    /// <summary>User-generated Personal Access Tokens.</summary>
    public DbSet<UserPatRecord> UserPats => this.Set<UserPatRecord>();

    /// <summary>Server-persisted refresh tokens.</summary>
    public DbSet<RefreshTokenRecord> RefreshTokens => this.Set<RefreshTokenRecord>();

    /// <summary>Per-client AI connection configurations.</summary>
    public DbSet<AiConnectionRecord> AiConnections => this.Set<AiConnectionRecord>();

    /// <summary>Repository-scope filters for crawl configurations.</summary>
    public DbSet<CrawlRepoFilterRecord> CrawlRepoFilters => this.Set<CrawlRepoFilterRecord>();

    /// <summary>Per-client AI reviewer finding dismissals.</summary>
    public DbSet<FindingDismissalRecord> FindingDismissals => this.Set<FindingDismissalRecord>();

    /// <summary>Per-client and per-crawl-config AI prompt overrides.</summary>
    public DbSet<PromptOverrideRecord> PromptOverrides => this.Set<PromptOverrideRecord>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MeisterProPRDbContext).Assembly);
    }
}
