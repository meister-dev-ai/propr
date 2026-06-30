// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class RetainedThreadCommentConfiguration : IEntityTypeConfiguration<RetainedThreadComment>
{
    public void Configure(EntityTypeBuilder<RetainedThreadComment> builder)
    {
        builder.ToTable("retained_thread_comments");

        builder.HasKey(comment => comment.Id);
        builder.Property(comment => comment.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(comment => comment.RetainedThreadId)
            .HasColumnName("retained_thread_id")
            .IsRequired();

        builder.Property(comment => comment.CommentId)
            .HasColumnName("comment_id")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(comment => comment.AuthorIdentity)
            .HasColumnName("author_identity")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(comment => comment.IsAiAuthored)
            .HasColumnName("is_ai_authored")
            .IsRequired();

        builder.Property(comment => comment.PublishedAt)
            .HasColumnName("published_at")
            .IsRequired();

        // The comment body is encrypted at rest and stored as opaque text.
        builder.Property(comment => comment.EncryptedText)
            .HasColumnName("encrypted_text")
            .HasColumnType("text")
            .IsRequired();

        // The review job that produced an AI comment, when its provenance is retained; null otherwise.
        builder.Property(comment => comment.OriginatingJobId)
            .HasColumnName("originating_job_id");

        builder.HasIndex(comment => comment.RetainedThreadId)
            .HasDatabaseName("ix_retained_thread_comments_thread_id");
    }
}
