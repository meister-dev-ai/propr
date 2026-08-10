// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class
    WebhookDeliveryQueueEntryEntityTypeConfiguration : IEntityTypeConfiguration<WebhookDeliveryQueueEntryRecord>
{
    public void Configure(EntityTypeBuilder<WebhookDeliveryQueueEntryRecord> builder)
    {
        builder.ToTable("webhook_delivery_queue");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.WebhookConfigurationId).HasColumnName("webhook_configuration_id").IsRequired();
        builder.Property(x => x.Provider).HasColumnName("provider").HasConversion<int>().IsRequired();
        builder.Property(x => x.PathKey).HasColumnName("path_key").HasMaxLength(128).IsRequired();
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(128).IsRequired();
        builder.Property(x => x.DeliveryKey).HasColumnName("delivery_key").HasMaxLength(256);
        builder.Property(x => x.HeadersJson).HasColumnName("headers").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.Payload).HasColumnName("payload").IsRequired();
        builder.Property(x => x.ReceivedAt).HasColumnName("received_at").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<int>().IsRequired();
        builder.Property(x => x.Attempts).HasColumnName("attempts").IsRequired();
        builder.Property(x => x.EligibleAt).HasColumnName("eligible_at").IsRequired();
        builder.Property(x => x.ClaimedBy).HasColumnName("claimed_by").HasMaxLength(256);
        builder.Property(x => x.ClaimedAt).HasColumnName("claimed_at");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(2048);

        builder.HasOne(x => x.WebhookConfiguration)
            .WithMany()
            .HasForeignKey(x => x.WebhookConfigurationId)
            .OnDelete(DeleteBehavior.Cascade);

        // The claim query's index: the oldest eligible entry, per status.
        builder.HasIndex(x => new { x.Status, x.EligibleAt })
            .HasDatabaseName("ix_webhook_delivery_queue_status_eligible_at");

        // A provider that retries a delivery sends the same key. Unique per configuration so a retry is
        // recognised as the delivery it already accepted rather than queued a second time.
        builder.HasIndex(x => new { x.WebhookConfigurationId, x.DeliveryKey })
            .HasDatabaseName("ix_webhook_delivery_queue_config_delivery_key")
            .IsUnique()
            .HasFilter("delivery_key IS NOT NULL");
    }
}
