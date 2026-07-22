// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

/// <summary>EF mapping for <see cref="BudgetEvent" /> — the persisted budget cap-reached transitions.</summary>
public sealed class BudgetEventConfiguration : IEntityTypeConfiguration<BudgetEvent>
{
    public void Configure(EntityTypeBuilder<BudgetEvent> builder)
    {
        builder.ToTable("budget_events");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
        builder.Property(e => e.EventType).HasColumnName("event_type").HasConversion<int>().IsRequired();
        builder.Property(e => e.Scope).HasColumnName("scope").HasConversion<int>().IsRequired();
        builder.Property(e => e.ThresholdUsd).HasColumnName("threshold_usd").HasPrecision(18, 6).IsRequired();
        builder.Property(e => e.SpentUsd).HasColumnName("spent_usd").HasPrecision(18, 6).IsRequired();
        builder.Property(e => e.JobId).HasColumnName("job_id").IsRequired(false);
        builder.Property(e => e.PullRequestId).HasColumnName("pull_request_id").IsRequired(false);
        builder.Property(e => e.IterationId).HasColumnName("iteration_id").IsRequired(false);
        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at").IsRequired();

        // The consumer polls per client by occurrence time.
        builder.HasIndex(e => new { e.ClientId, e.OccurredAt });
    }
}
