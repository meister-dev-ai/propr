using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class CrawlRepoFilterEntityTypeConfiguration : IEntityTypeConfiguration<CrawlRepoFilterRecord>
{
    public void Configure(EntityTypeBuilder<CrawlRepoFilterRecord> builder)
    {
        builder.ToTable("crawl_repo_filters");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.CrawlConfigurationId)
            .HasColumnName("crawl_configuration_id")
            .IsRequired();

        builder.Property(x => x.RepositoryName)
            .HasColumnName("repository_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.TargetBranchPatterns)
            .HasColumnName("target_branch_patterns")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.HasOne(x => x.CrawlConfiguration)
            .WithMany(c => c.RepoFilters)
            .HasForeignKey(x => x.CrawlConfigurationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.CrawlConfigurationId, x.RepositoryName })
            .IsUnique()
            .HasDatabaseName("ix_crawl_repo_filters_config_repo");
    }
}
