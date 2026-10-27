using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AllowExternalIdentitiesWithoutTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "tenant_id",
                table: "external_identities",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "sso_provider_id",
                table: "external_identities",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_platform_subject",
                table: "external_identities",
                columns: new[] { "issuer", "subject" },
                unique: true,
                filter: "tenant_id IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_external_identities_tenant_and_provider",
                table: "external_identities",
                sql: "(tenant_id IS NULL) = (sso_provider_id IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The links of platform administrators carry no tenant and no provider, so they cannot satisfy the
            // NOT NULL constraints restored below. The accounts keep their rights; only the links are removed. With no
            // null left, the columns need no default value either, so none is set.
            migrationBuilder.Sql("DELETE FROM external_identities WHERE tenant_id IS NULL;");

            migrationBuilder.DropIndex(
                name: "ix_external_identities_platform_subject",
                table: "external_identities");

            migrationBuilder.DropCheckConstraint(
                name: "ck_external_identities_tenant_and_provider",
                table: "external_identities");

            migrationBuilder.AlterColumn<Guid>(
                name: "tenant_id",
                table: "external_identities",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "sso_provider_id",
                table: "external_identities",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
