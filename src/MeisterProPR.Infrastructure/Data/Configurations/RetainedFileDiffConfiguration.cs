// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class RetainedFileDiffConfiguration : IEntityTypeConfiguration<RetainedFileDiff>
{
    public void Configure(EntityTypeBuilder<RetainedFileDiff> builder)
    {
        builder.ToTable("retained_file_diffs");

        builder.HasKey(diff => diff.Id);
        builder.Property(diff => diff.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(diff => diff.RetainedPullRequestId)
            .HasColumnName("retained_pull_request_id")
            .IsRequired();

        builder.Property(diff => diff.RevisionKey)
            .HasColumnName("revision_key")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(diff => diff.FilePath)
            .HasColumnName("file_path")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(diff => diff.ChangeType)
            .HasColumnName("change_type")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(diff => diff.IsBinary)
            .HasColumnName("is_binary")
            .IsRequired();

        // The unified diff is encrypted at rest and stored as opaque text.
        builder.Property(diff => diff.EncryptedUnifiedDiff)
            .HasColumnName("encrypted_unified_diff")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(diff => diff.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        // At most one retained diff per file within a review increment of a retained pull request.
        builder.HasIndex(diff => new { diff.RetainedPullRequestId, diff.RevisionKey, diff.FilePath })
            .IsUnique()
            .HasDatabaseName("uq_retained_file_diffs_identity");
    }
}
