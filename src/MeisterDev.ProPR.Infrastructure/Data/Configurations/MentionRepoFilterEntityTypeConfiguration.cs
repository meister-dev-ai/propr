// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class MentionRepoFilterEntityTypeConfiguration
    : IEntityTypeConfiguration<MentionRepoFilterRecord>
{
    public void Configure(EntityTypeBuilder<MentionRepoFilterRecord> builder)
    {
        builder.ToTable("mention_repo_filters");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.MentionConfigurationId)
            .HasColumnName("mention_configuration_id")
            .IsRequired();

        builder.Property(x => x.RepositoryId)
            .HasColumnName("repository_id")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(x => x.SourceProvider)
            .HasColumnName("source_provider")
            .HasMaxLength(64)
            .IsRequired(false);

        builder.Property(x => x.CanonicalSourceRef)
            .HasColumnName("canonical_source_ref")
            .HasMaxLength(512)
            .IsRequired(false);

        builder.Property(x => x.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(x => x.ClaimedAt)
            .HasColumnName("claimed_at")
            .IsRequired();

        builder.HasOne(x => x.MentionConfiguration)
            .WithMany(c => c.RepoFilters)
            .HasForeignKey(x => x.MentionConfigurationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Keyed on the repository id rather than the canonical source reference the crawl filter uses,
        // because the id is what the scan matches on and listing one repository twice in a configuration
        // would make it ambiguous which row a later edit is changing.
        //
        // The rule is enforced by uq_mention_repo_filters_config_repository, which the migration creates
        // over lower(repository_id). The uniqueness the application relies on is case-insensitive, because
        // the scan matches repository ids that way and an edit groups them that way, so a case-sensitive
        // rule would admit a pair the next edit then throws on. The column pair is not modelled alongside
        // it: EF cannot express an index over lower(), and a plain copy would be written on every insert
        // while enforcing nothing.
        //
        // The foreign key is indexed here, in the model, and the uniqueness rule stays migration-only. Both
        // halves are deliberate: the expression index leads with this same column and would serve these
        // reads, but the model cannot see it, so an unindexed key would be scaffolded back into the next
        // migration under a generated name.
        builder.HasIndex(x => x.MentionConfigurationId)
            .HasDatabaseName("ix_mention_repo_filters_configuration_id");
    }
}
