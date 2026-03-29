using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class FindingDismissalEntityTypeConfiguration : IEntityTypeConfiguration<FindingDismissalRecord>
{
    public void Configure(EntityTypeBuilder<FindingDismissalRecord> builder)
    {
        builder.ToTable("finding_dismissals");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(x => x.PatternText)
            .HasColumnName("pattern_text")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.Label)
            .HasColumnName("label")
            .HasMaxLength(300)
            .IsRequired(false);

        builder.Property(x => x.OriginalMessage)
            .HasColumnName("original_message")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasOne(x => x.Client)
            .WithMany()
            .HasForeignKey(x => x.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.ClientId)
            .HasDatabaseName("idx_finding_dismissals_client_id");
    }
}
