using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterProPR.Infrastructure.Data.Configurations;

internal sealed class UserClientRoleEntityTypeConfiguration : IEntityTypeConfiguration<UserClientRoleRecord>
{
    public void Configure(EntityTypeBuilder<UserClientRoleRecord> builder)
    {
        builder.ToTable("user_client_roles");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(r => r.ClientId).HasColumnName("client_id").IsRequired();
        builder.Property(r => r.Role).HasColumnName("role").HasConversion<string>().IsRequired();
        builder.Property(r => r.AssignedAt).HasColumnName("assigned_at").IsRequired();

        builder.HasIndex(r => new { r.UserId, r.ClientId }).HasDatabaseName("ix_user_client_roles_user_client").IsUnique();
    }
}
