// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class RunnerIngestReceiptConfiguration : IEntityTypeConfiguration<RunnerIngestReceipt>
{
    public void Configure(EntityTypeBuilder<RunnerIngestReceipt> builder)
    {
        builder.ToTable("runner_ingest_receipts");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(r => r.JobId).HasColumnName("job_id").IsRequired();
        builder.Property(r => r.Sequence).HasColumnName("sequence").IsRequired();

        builder.Property(r => r.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(r => r.ReceivedAt).HasColumnName("received_at").IsRequired();

        // The uniqueness is the mechanism, not a safeguard on top of one: two deliveries of the same batch
        // arriving together both try to insert, and the loser learns from the index that it lost.
        builder.HasIndex(r => new { r.JobId, r.Sequence })
            .IsUnique()
            .HasDatabaseName("ix_runner_ingest_receipts_job_sequence");

        builder.HasIndex(r => new { r.JobId, r.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("ix_runner_ingest_receipts_job_key");
    }
}
