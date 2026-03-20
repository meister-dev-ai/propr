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

    /// <summary>Mention reply jobs table.</summary>
    public DbSet<MentionReplyJob> MentionReplyJobs => this.Set<MentionReplyJob>();

    /// <summary>Mention project scan watermarks table.</summary>
    public DbSet<MentionProjectScan> MentionProjectScans => this.Set<MentionProjectScan>();

    /// <summary>Mention per-PR scan watermarks table.</summary>
    public DbSet<MentionPrScan> MentionPrScans => this.Set<MentionPrScan>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MeisterProPRDbContext).Assembly);
    }
}