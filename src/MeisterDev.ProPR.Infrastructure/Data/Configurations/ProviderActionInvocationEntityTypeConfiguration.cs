// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class ProviderActionInvocationEntityTypeConfiguration
    : IEntityTypeConfiguration<ProviderActionInvocationRecord>
{
    public void Configure(EntityTypeBuilder<ProviderActionInvocationRecord> builder)
    {
        // The state decides whether a run can still be continued, closed or expired, and every read keys on it.
        // The two write paths already refuse a value outside the set; the constraint is what holds for a row
        // written by anything else. The set is ordered so the constraint text is stable across builds.
        var states = string.Join(
            ", ",
            ProviderInvocationState.All.Order(StringComparer.Ordinal).Select(state => $"'{state}'"));

        builder.ToTable(
            "ai_provider_action_invocations",
            table => table.HasCheckConstraint(
                "ck_ai_provider_action_invocations_state_is_known",
                $"state IN ({states})"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.ConnectionProfileId).HasColumnName("connection_profile_id");
        builder.Property(x => x.ConnectionDisplayName)
            .HasColumnName("connection_display_name")
            .HasMaxLength(200)
            .IsRequired();
        builder.Property(x => x.AddInKey)
            .HasColumnName("add_in_key")
            .HasMaxLength(ProviderDeclaration.MaximumKeyLength)
            .IsRequired();
        builder.Property(x => x.ActionId)
            .HasColumnName("action_id")
            .HasMaxLength(ProviderHostLimits.MaximumActionIdLength)
            .IsRequired();
        builder.Property(x => x.InitiatingAdminId).HasColumnName("initiating_admin_id");
        builder.Property(x => x.State).HasColumnName("state").HasMaxLength(20).IsRequired();
        builder.Property(x => x.TerminalMessage)
            .HasColumnName("terminal_message")
            .HasMaxLength(ProviderHostLimits.MaximumMessageLength);
        builder.Property(x => x.WaitingFor)
            .HasColumnName("waiting_for")
            .HasMaxLength(ProviderHostLimits.MaximumMessageLength);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");

        // The run survives the connection it acted on. It records who started what, when, and how it ended, and
        // deleting a connection took that with it. The reference is cleared instead, and the connection's name
        // is on the row so the run still says what it was against. Restricting the delete was the alternative
        // and it is worse: an operator could not remove a connection that had ever been acted on.
        builder.HasOne<AiConnectionProfileRecord>()
            .WithMany()
            .HasForeignKey(x => x.ConnectionProfileId)
            .OnDelete(DeleteBehavior.SetNull);

        // Read by the sweep that expires invocations whose window has closed, which is every pending row and no
        // other.
        builder.HasIndex(x => x.ExpiresAt)
            .HasDatabaseName("ix_ai_provider_action_invocations_pending_expires_at")
            .HasFilter("state = 'Pending'");
    }
}
