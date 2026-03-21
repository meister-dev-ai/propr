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

        builder.Property(c => c.Key)
            .HasColumnName("key")
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

        builder.Property(c => c.AdoTenantId)
            .HasColumnName("ado_tenant_id")
            .IsRequired(false);

        builder.Property(c => c.AdoClientId)
            .HasColumnName("ado_client_id")
            .IsRequired(false);

        builder.Property(c => c.AdoClientSecret)
            .HasColumnName("ado_client_secret")
            .IsRequired(false);

        builder.Property(c => c.ReviewerId)
            .HasColumnName("reviewer_id")
            .IsRequired(false);

        builder.HasIndex(c => c.Key)
            .IsUnique()
            .HasDatabaseName("ix_clients_key");
    }
}
