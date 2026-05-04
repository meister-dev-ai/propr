// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class WebhookConfigurationEntityTypeConfiguration : IEntityTypeConfiguration<WebhookConfigurationRecord>
{
    public void Configure(EntityTypeBuilder<WebhookConfigurationRecord> builder)
    {
        builder.ToTable("webhook_configurations");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(c => c.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(c => c.ProviderType)
            .HasColumnName("provider_type")
            .HasConversion<int>()
            .HasDefaultValue(WebhookProviderType.AzureDevOps)
            .IsRequired();

        builder.Property(c => c.PublicPathKey)
            .HasColumnName("public_path_key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(c => c.OrganizationScopeId)
            .HasColumnName("organization_scope_id")
            .IsRequired(false);

        builder.Property(c => c.OrganizationUrl)
            .HasColumnName("organization_url")
            .IsRequired();

        builder.Property(c => c.ProjectId)
            .HasColumnName("project_id")
            .IsRequired();

        builder.Property(c => c.SecretCiphertext)
            .HasColumnName("secret_ciphertext")
            .IsRequired();

        builder.Property(c => c.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true);

        builder.Property(c => c.EnabledEvents)
            .HasColumnName("enabled_events")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(c => c.ReviewTemperature)
            .HasColumnName("review_temperature")
            .IsRequired(false);

        builder.Property(c => c.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasOne(c => c.Client)
            .WithMany()
            .HasForeignKey(c => c.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(c => c.OrganizationScope)
            .WithMany()
            .HasForeignKey(c => c.OrganizationScopeId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(c => c.RepoFilters)
            .WithOne(f => f.WebhookConfiguration)
            .HasForeignKey(f => f.WebhookConfigurationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(c => c.DeliveryLogs)
            .WithOne(log => log.WebhookConfiguration)
            .HasForeignKey(log => log.WebhookConfigurationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => c.ClientId).HasDatabaseName("ix_webhook_configurations_client_id");
        builder.HasIndex(c => c.OrganizationScopeId).HasDatabaseName("ix_webhook_configurations_organization_scope_id");
        builder.HasIndex(c => c.PublicPathKey).IsUnique().HasDatabaseName("ux_webhook_configurations_public_path_key");
        builder.HasIndex(c => c.IsActive)
            .HasDatabaseName("ix_webhook_configurations_active")
            .HasFilter("is_active = true");
    }
}
