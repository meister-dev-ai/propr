// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class
    WebhookDeliveryLogEntryEntityTypeConfiguration : IEntityTypeConfiguration<WebhookDeliveryLogEntryRecord>
{
    public void Configure(EntityTypeBuilder<WebhookDeliveryLogEntryRecord> builder)
    {
        builder.ToTable("webhook_delivery_log_entries");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.WebhookConfigurationId)
            .HasColumnName("webhook_configuration_id")
            .IsRequired();

        builder.Property(x => x.ReceivedAt)
            .HasColumnName("received_at")
            .IsRequired();

        builder.Property(x => x.EventType)
            .HasColumnName("event_type")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.DeliveryOutcome)
            .HasColumnName("delivery_outcome")
            .HasConversion<int>()
            .IsRequired();

        builder.Property(x => x.HttpStatusCode)
            .HasColumnName("http_status_code")
            .IsRequired();

        builder.Property(x => x.RepositoryId)
            .HasColumnName("repository_id")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(x => x.PullRequestId)
            .HasColumnName("pull_request_id")
            .IsRequired(false);

        builder.Property(x => x.SourceBranch)
            .HasColumnName("source_branch")
            .HasMaxLength(512)
            .IsRequired(false);

        builder.Property(x => x.TargetBranch)
            .HasColumnName("target_branch")
            .HasMaxLength(512)
            .IsRequired(false);

        builder.Property(x => x.ActionSummaries)
            .HasColumnName("action_summaries")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(x => x.FailureReason)
            .HasColumnName("failure_reason")
            .HasMaxLength(2048)
            .IsRequired(false);

        builder.Property(x => x.FailureCategory)
            .HasColumnName("failure_category")
            .HasMaxLength(64)
            .IsRequired(false);

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasOne(x => x.WebhookConfiguration)
            .WithMany(c => c.DeliveryLogs)
            .HasForeignKey(x => x.WebhookConfigurationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.WebhookConfigurationId, x.ReceivedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_webhook_delivery_log_entries_config_received_at");
    }
}
