using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class AiConnectionEntityTypeConfiguration : IEntityTypeConfiguration<AiConnectionRecord>
{
    public void Configure(EntityTypeBuilder<AiConnectionRecord> builder)
    {
        builder.ToTable("ai_connections");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(x => x.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.EndpointUrl)
            .HasColumnName("endpoint_url")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(x => x.Models)
            .HasColumnName("models")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(x => x.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(x => x.ActiveModel)
            .HasColumnName("active_model")
            .HasMaxLength(200)
            .IsRequired(false);

        builder.Property(x => x.ApiKey)
            .HasColumnName("api_key")
            .HasMaxLength(500)
            .IsRequired(false);

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(x => x.ModelCategory)
            .HasColumnName("model_category")
            .IsRequired(false);

        builder.HasOne(x => x.Client)
            .WithMany()
            .HasForeignKey(x => x.ClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.ClientId, x.DisplayName })
            .IsUnique()
            .HasDatabaseName("ix_ai_connections_client_id_display_name");

        // Partial unique index: at most one active connection per client
        builder.HasIndex(x => x.ClientId)
            .HasDatabaseName("ix_ai_connections_client_id_active")
            .HasFilter("is_active = true")
            .IsUnique();

        // Partial unique index: at most one connection per model category per client
        builder.HasIndex(x => new { x.ClientId, x.ModelCategory })
            .HasDatabaseName("ix_ai_connections_client_id_model_category")
            .HasFilter("model_category IS NOT NULL")
            .IsUnique();
    }
}
