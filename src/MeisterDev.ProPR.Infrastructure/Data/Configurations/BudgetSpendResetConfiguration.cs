// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

/// <summary>EF mapping for <see cref="BudgetSpendReset" /> — the persisted manual spend resets.</summary>
public sealed class BudgetSpendResetConfiguration : IEntityTypeConfiguration<BudgetSpendReset>
{
    public void Configure(EntityTypeBuilder<BudgetSpendReset> builder)
    {
        builder.ToTable("budget_spend_resets");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
        builder.Property(e => e.PeriodStart).HasColumnName("period_start").IsRequired();
        builder.Property(e => e.TopUpSoftCapUsd).HasColumnName("top_up_soft_cap_usd").HasPrecision(18, 6);
        builder.Property(e => e.TopUpHardCapUsd).HasColumnName("top_up_hard_cap_usd").HasPrecision(18, 6);
        builder.Property(e => e.EffectiveSoftCapBeforeUsd).HasColumnName("effective_soft_cap_before_usd").HasPrecision(18, 6);
        builder.Property(e => e.EffectiveSoftCapAfterUsd).HasColumnName("effective_soft_cap_after_usd").HasPrecision(18, 6);
        builder.Property(e => e.EffectiveHardCapBeforeUsd).HasColumnName("effective_hard_cap_before_usd").HasPrecision(18, 6);
        builder.Property(e => e.EffectiveHardCapAfterUsd).HasColumnName("effective_hard_cap_after_usd").HasPrecision(18, 6);
        builder.Property(e => e.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(e => e.PerformedAt).HasColumnName("performed_at").IsRequired();

        // Every read is "the resets this client got in this period" (or a bounded range of them).
        builder.HasIndex(e => new { e.ClientId, e.PeriodStart });
    }
}
