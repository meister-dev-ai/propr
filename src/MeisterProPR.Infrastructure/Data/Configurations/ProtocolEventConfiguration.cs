// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ProtocolEventConfiguration : IEntityTypeConfiguration<ProtocolEvent>
{
    public void Configure(EntityTypeBuilder<ProtocolEvent> builder)
    {
        builder.ToTable("protocol_events");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.ProtocolId).HasColumnName("protocol_id").IsRequired();
        builder.Property(e => e.Kind).HasColumnName("kind").IsRequired();
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at").IsRequired();
        builder.Property(e => e.InputTokens).HasColumnName("input_tokens").IsRequired(false);
        builder.Property(e => e.OutputTokens).HasColumnName("output_tokens").IsRequired(false);
        builder.Property(e => e.InputTextSample)
            .HasColumnName("input_text_sample")
            .HasColumnType("text")
            .IsRequired(false);
        builder.Property(e => e.SystemPrompt)
            .HasColumnName("system_prompt")
            .HasColumnType("text")
            .IsRequired(false);
        builder.Property(e => e.OutputSummary)
            .HasColumnName("output_summary")
            .HasColumnType("text")
            .IsRequired(false);
        builder.Property(e => e.Error).HasColumnName("error").IsRequired(false);

        builder.HasIndex(e => e.ProtocolId).HasDatabaseName("ix_protocol_events_protocol_id");
    }
}
