// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

/// <summary>EF mapping for <see cref="CodeInsightEvent" />: the persisted quality-condition transitions.</summary>
internal sealed class CodeInsightEventConfiguration : IEntityTypeConfiguration<CodeInsightEvent>
{
    public void Configure(EntityTypeBuilder<CodeInsightEvent> builder)
    {
        builder.ToTable("code_insight_events");

        builder.HasKey(evt => evt.Id);
        builder.Property(evt => evt.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(evt => evt.ClientId).HasColumnName("client_id").IsRequired();

        builder.Property(evt => evt.RepositoryId)
            .HasColumnName("repository_id")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(evt => evt.FilePath)
            .HasColumnName("file_path")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(evt => evt.EventType).HasColumnName("event_type").HasConversion<int>().IsRequired();
        builder.Property(evt => evt.State).HasColumnName("state").HasConversion<int>().IsRequired();

        builder.Property(evt => evt.Metric)
            .HasColumnName("metric")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(evt => evt.Direction).HasColumnName("direction").HasConversion<int>().IsRequired();
        builder.Property(evt => evt.ObservedValue).HasColumnName("observed_value").IsRequired();
        builder.Property(evt => evt.PreviousValue).HasColumnName("previous_value").IsRequired(false);
        builder.Property(evt => evt.Magnitude).HasColumnName("magnitude").IsRequired();
        builder.Property(evt => evt.ThresholdValue).HasColumnName("threshold_value").IsRequired();
        builder.Property(evt => evt.SampleSize).HasColumnName("sample_size").IsRequired();
        builder.Property(evt => evt.WindowFrom).HasColumnName("window_from").IsRequired();
        builder.Property(evt => evt.WindowTo).HasColumnName("window_to").IsRequired();
        builder.Property(evt => evt.OccurredAt).HasColumnName("occurred_at").IsRequired();

        // The fire-once lookup: the latest row for one scope and condition is that condition's current state.
        builder.HasIndex(evt => new
            {
                evt.ClientId,
                evt.RepositoryId,
                evt.FilePath,
                evt.EventType,
                evt.OccurredAt,
            })
            .HasDatabaseName("ix_code_insight_events_scope_condition");

        // The consumer's poll shape: a client's events since a point in time.
        builder.HasIndex(evt => new { evt.ClientId, evt.OccurredAt })
            .HasDatabaseName("ix_code_insight_events_client_occurred");

        // Removing a client removes the events raised about it.
        builder.HasOne<ClientRecord>()
            .WithMany()
            .HasForeignKey(evt => evt.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
