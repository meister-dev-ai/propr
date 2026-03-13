using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class CrawlConfigurationEntityTypeConfiguration : IEntityTypeConfiguration<CrawlConfigurationRecord>
{
    public void Configure(EntityTypeBuilder<CrawlConfigurationRecord> builder)
    {
        builder.ToTable("crawl_configurations");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(c => c.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(c => c.OrganizationUrl)
            .HasColumnName("organization_url")
            .IsRequired();

        builder.Property(c => c.ProjectId)
            .HasColumnName("project_id")
            .IsRequired();

        builder.Property(c => c.CrawlIntervalSeconds)
            .HasColumnName("crawl_interval_seconds")
            .HasDefaultValue(60);

        builder.Property(c => c.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true);

        builder.Property(c => c.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasOne(c => c.Client)
            .WithMany()
            .HasForeignKey(c => c.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => c.ClientId).HasDatabaseName("ix_crawl_configurations_client_id");

        builder.HasIndex(c => new { c.ClientId, c.OrganizationUrl, c.ProjectId })
            .IsUnique()
            .HasDatabaseName("ix_crawl_configurations_unique_config");
        builder.HasIndex(c => c.IsActive)
            .HasDatabaseName("ix_crawl_configurations_active")
            .HasFilter("is_active = true");
    }
}