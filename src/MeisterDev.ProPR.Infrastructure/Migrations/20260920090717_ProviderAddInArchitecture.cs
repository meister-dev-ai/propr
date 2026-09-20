// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProviderAddInArchitecture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // First, because the statement behind this reads the logical-model protocol mode while it is still
            // an integer and changes its type in the same breath.
            ProviderAddInDataMigration.NameLogicalModelProtocolModes(migrationBuilder);

            migrationBuilder.AlterColumn<string>(
                name: "protocol_mode",
                table: "ai_purpose_bindings",
                type: "character varying(129)",
                maxLength: 129,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<string>(
                name: "provider_kind",
                table: "ai_connection_profiles",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<string>(
                name: "protected_secret",
                table: "ai_connection_profiles",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(16000)",
                oldMaxLength: 16000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "auth_mode",
                table: "ai_connection_profiles",
                type: "character varying(129)",
                maxLength: 129,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "credential_authorized_at",
                table: "ai_connection_profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "credential_health",
                table: "ai_connection_profiles",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "credential_health_cause",
                table: "ai_connection_profiles",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "credential_health_changed_at",
                table: "ai_connection_profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "credential_owner_admin_id",
                table: "ai_connection_profiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "credential_owner_display_name",
                table: "ai_connection_profiles",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_settings",
                table: "ai_connection_profiles",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ai_provider_action_invocations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_profile_id = table.Column<Guid>(type: "uuid", nullable: true),
                    // The empty default is carried over deliberately. An installation that upgraded through
                    // the per-family migrations has it, because AddColumn on a populated table leaves one
                    // behind, and a fresh install has to match it or the two schemas drift.
                    connection_display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, defaultValue: ""),
                    add_in_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    action_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    initiating_admin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    terminal_message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    waiting_for = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_provider_action_invocations", x => x.id);
                    table.CheckConstraint("ck_ai_provider_action_invocations_state_is_known", "state IN ('Completed', 'Expired', 'Failed', 'Pending')");
                    table.ForeignKey(
                        name: "FK_ai_provider_action_invocations_ai_connection_profiles_conne~",
                        column: x => x.connection_profile_id,
                        principalTable: "ai_connection_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ai_provider_add_in_activations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    family_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    activated_by_admin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    activated_by_display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_provider_add_in_activations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_provider_keyed_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    add_in_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entry_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    protected_value = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acting_principal_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_provider_keyed_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_provider_resource_leases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    add_in_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resource_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    connection_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    acquired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_provider_resource_leases", x => x.id);
                    table.ForeignKey(
                        name: "FK_ai_provider_resource_leases_ai_connection_profiles_connecti~",
                        column: x => x.connection_profile_id,
                        principalTable: "ai_connection_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_provider_action_invocations_connection_profile_id",
                table: "ai_provider_action_invocations",
                column: "connection_profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_provider_action_invocations_pending_expires_at",
                table: "ai_provider_action_invocations",
                column: "expires_at",
                filter: "state = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_ai_provider_add_in_activations_family_key",
                table: "ai_provider_add_in_activations",
                column: "family_key");

            migrationBuilder.CreateIndex(
                name: "ux_ai_provider_add_in_activations_content_hash",
                table: "ai_provider_add_in_activations",
                column: "content_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_provider_keyed_entries_add_in_key_entry_key",
                table: "ai_provider_keyed_entries",
                columns: new[] { "add_in_key", "entry_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_provider_keyed_entries_expires_at",
                table: "ai_provider_keyed_entries",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_ai_provider_resource_leases_add_in_key_resource_name",
                table: "ai_provider_resource_leases",
                columns: new[] { "add_in_key", "resource_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_provider_resource_leases_connection_profile_id",
                table: "ai_provider_resource_leases",
                column: "connection_profile_id");
            // After the schema, because the qualified values the families move onto are longer than the columns
            // were and the rows they write live in tables created above.
            ProviderAddInDataMigration.Forward(migrationBuilder);

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_logical_models_protocol_mode_is_a_name",
                table: "ai_logical_models",
                sql: "protocol_mode !~ '^[0-9]+$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_logical_model_overrides_protocol_mode_is_a_name",
                table: "ai_logical_model_overrides",
                sql: "protocol_mode !~ '^[0-9]+$'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The mirror of Up: the constraints go before the values stop being names, the families go back onto
            // the vocabulary they arrived with, and the numbering runs last because it reads those names.
            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_logical_models_protocol_mode_is_a_name",
                table: "ai_logical_models");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_logical_model_overrides_protocol_mode_is_a_name",
                table: "ai_logical_model_overrides");

            ProviderAddInDataMigration.Back(migrationBuilder);

            migrationBuilder.DropTable(
                name: "ai_provider_action_invocations");

            migrationBuilder.DropTable(
                name: "ai_provider_add_in_activations");

            migrationBuilder.DropTable(
                name: "ai_provider_keyed_entries");

            migrationBuilder.DropTable(
                name: "ai_provider_resource_leases");

            migrationBuilder.DropColumn(
                name: "credential_authorized_at",
                table: "ai_connection_profiles");

            migrationBuilder.DropColumn(
                name: "credential_health",
                table: "ai_connection_profiles");

            migrationBuilder.DropColumn(
                name: "credential_health_cause",
                table: "ai_connection_profiles");

            migrationBuilder.DropColumn(
                name: "credential_health_changed_at",
                table: "ai_connection_profiles");

            migrationBuilder.DropColumn(
                name: "credential_owner_admin_id",
                table: "ai_connection_profiles");

            migrationBuilder.DropColumn(
                name: "credential_owner_display_name",
                table: "ai_connection_profiles");

            migrationBuilder.DropColumn(
                name: "provider_settings",
                table: "ai_connection_profiles");

            migrationBuilder.AlterColumn<string>(
                name: "protocol_mode",
                table: "ai_purpose_bindings",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(129)",
                oldMaxLength: 129);

            migrationBuilder.AlterColumn<string>(
                name: "provider_kind",
                table: "ai_connection_profiles",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "protected_secret",
                table: "ai_connection_profiles",
                type: "character varying(16000)",
                maxLength: 16000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "auth_mode",
                table: "ai_connection_profiles",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(129)",
                oldMaxLength: 129);

            ProviderAddInDataMigration.NumberLogicalModelProtocolModes(migrationBuilder);
        }
    }
}
