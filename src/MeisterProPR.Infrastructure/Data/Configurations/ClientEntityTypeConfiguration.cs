// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class ClientEntityTypeConfiguration : IEntityTypeConfiguration<ClientRecord>
{
    public void Configure(EntityTypeBuilder<ClientRecord> builder)
    {
        builder.ToTable("clients");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(c => c.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(c => c.DisplayName)
            .HasColumnName("display_name")
            .IsRequired();

        builder.Property(c => c.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true);

        builder.Property(c => c.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(c => c.CommentResolutionBehavior)
            .HasColumnName("comment_resolution_behavior")
            .HasConversion<int>()
            .HasDefaultValue(CommentResolutionBehavior.Silent)
            .HasSentinel(CommentResolutionBehavior.Silent);

        builder.Property(c => c.DefaultReviewStrategy)
            .HasColumnName("default_review_strategy")
            .HasConversion<int>()
            .HasDefaultValue(ReviewStrategy.FileByFile)
            .HasSentinel(ReviewStrategy.FileByFile);

        builder.Property(c => c.CustomSystemMessage)
            .HasColumnName("custom_system_message")
            .IsRequired(false);

        builder.Property(c => c.DefaultReviewPipelineProfileId)
            .HasColumnName("default_review_pipeline_profile_id")
            .HasMaxLength(128)
            .IsRequired(false);

        builder.Property(c => c.DefaultReviewPipelineProfileUpdatedAtUtc)
            .HasColumnName("default_review_pipeline_profile_updated_at_utc")
            .IsRequired(false);

        builder.Property(c => c.ScmCommentPostingEnabled)
            .HasColumnName("scm_comment_posting_enabled")
            .HasDefaultValue(true);

        builder.Property(c => c.EnableProRV)
            .HasColumnName("enable_prorv")
            .HasDefaultValue(false);

        builder.Property(c => c.EnableEvidenceBackedVerification)
            .HasColumnName("enable_evidence_backed_verification")
            .HasDefaultValue(false);

        builder.Property(c => c.EnableMultiPassUnion)
            .HasColumnName("enable_multi_pass_union")
            .HasDefaultValue(false);

        builder.Property(c => c.MultiPassUnionPassCount)
            .HasColumnName("multi_pass_union_pass_count");

        builder.HasIndex(c => c.TenantId)
            .HasDatabaseName("ix_clients_tenant_id");

        builder.HasOne(c => c.Tenant)
            .WithMany(t => t.Clients)
            .HasForeignKey(c => c.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
