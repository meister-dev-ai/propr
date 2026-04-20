// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderNeutralScmClientModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "code_review_platform_kind",
                table: "review_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "external_code_review_id",
                table: "review_jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "host_base_url",
                table: "review_jobs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "provider",
                table: "review_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "provider_revision_id",
                table: "review_jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repository_owner_or_namespace",
                table: "review_jobs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repository_project_path",
                table: "review_jobs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_patch_identity",
                table: "review_jobs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "revision_base_sha",
                table: "review_jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "revision_head_sha",
                table: "review_jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "revision_start_sha",
                table: "review_jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "code_review_platform_kind",
                table: "mention_reply_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "comment_author_display_name",
                table: "mention_reply_jobs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "comment_author_external_user_id",
                table: "mention_reply_jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "comment_author_is_bot",
                table: "mention_reply_jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "comment_author_login",
                table: "mention_reply_jobs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "comment_published_at",
                table: "mention_reply_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_code_review_id",
                table: "mention_reply_jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "host_base_url",
                table: "mention_reply_jobs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "provider",
                table: "mention_reply_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "repository_owner_or_namespace",
                table: "mention_reply_jobs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repository_project_path",
                table: "mention_reply_jobs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "thread_file_path",
                table: "mention_reply_jobs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "thread_line_number",
                table: "mention_reply_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "client_scm_connections" (
                    "id" uuid NOT NULL,
                    "client_id" uuid NOT NULL,
                    "provider" integer NOT NULL,
                    "host_base_url" character varying(512) NOT NULL,
                    "authentication_kind" integer NOT NULL,
                    "display_name" character varying(200) NOT NULL,
                    "encrypted_secret_material" text NOT NULL,
                    "verification_status" character varying(64) NOT NULL DEFAULT 'unknown',
                    "is_active" boolean NOT NULL DEFAULT TRUE,
                    "created_at" timestamp with time zone NOT NULL,
                    "updated_at" timestamp with time zone NOT NULL,
                    "last_verified_at" timestamp with time zone,
                    "last_verification_error" text,
                    CONSTRAINT "PK_client_scm_connections" PRIMARY KEY ("id"),
                    CONSTRAINT "FK_client_scm_connections_clients_client_id" FOREIGN KEY ("client_id") REFERENCES "clients" ("id") ON DELETE CASCADE
                );
                """);

            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "client_reviewer_identities" (
                    "id" uuid NOT NULL,
                    "client_id" uuid NOT NULL,
                    "connection_id" uuid NOT NULL,
                    "provider" integer NOT NULL,
                    "external_user_id" character varying(256) NOT NULL,
                    "login" character varying(256) NOT NULL,
                    "display_name" character varying(256) NOT NULL,
                    "is_bot" boolean NOT NULL DEFAULT FALSE,
                    "updated_at" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_client_reviewer_identities" PRIMARY KEY ("id"),
                    CONSTRAINT "FK_client_reviewer_identities_client_scm_connections_connectio~" FOREIGN KEY ("connection_id") REFERENCES "client_scm_connections" ("id") ON DELETE CASCADE,
                    CONSTRAINT "FK_client_reviewer_identities_clients_client_id" FOREIGN KEY ("client_id") REFERENCES "clients" ("id") ON DELETE CASCADE
                );
                """);

            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "client_scm_scopes" (
                    "id" uuid NOT NULL,
                    "client_id" uuid NOT NULL,
                    "connection_id" uuid NOT NULL,
                    "scope_type" character varying(64) NOT NULL,
                    "external_scope_id" character varying(256) NOT NULL,
                    "scope_path" character varying(512) NOT NULL,
                    "display_name" character varying(256) NOT NULL,
                    "verification_status" character varying(64) NOT NULL DEFAULT 'unknown',
                    "is_enabled" boolean NOT NULL DEFAULT TRUE,
                    "last_verified_at" timestamp with time zone,
                    "last_verification_error" text,
                    "created_at" timestamp with time zone NOT NULL,
                    "updated_at" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_client_scm_scopes" PRIMARY KEY ("id"),
                    CONSTRAINT "FK_client_scm_scopes_client_scm_connections_connection_id" FOREIGN KEY ("connection_id") REFERENCES "client_scm_connections" ("id") ON DELETE CASCADE,
                    CONSTRAINT "FK_client_scm_scopes_clients_client_id" FOREIGN KEY ("client_id") REFERENCES "clients" ("id") ON DELETE CASCADE
                );
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "ix_review_jobs_client_provider_review"
                    ON "review_jobs" ("client_id", "provider", "repository_id", "external_code_review_id");
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_client_reviewer_identities_client_id"
                    ON "client_reviewer_identities" ("client_id");
                """);

            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX IF NOT EXISTS "ix_client_reviewer_identities_connection_id"
                    ON "client_reviewer_identities" ("connection_id");
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "ix_client_scm_connections_client_provider_host"
                    ON "client_scm_connections" ("client_id", "provider", "host_base_url");
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_client_scm_scopes_client_id"
                    ON "client_scm_scopes" ("client_id");
                """);

            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX IF NOT EXISTS "ix_client_scm_scopes_connection_external_scope_id"
                    ON "client_scm_scopes" ("connection_id", "external_scope_id");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "client_reviewer_identities");

            migrationBuilder.DropTable(
                name: "client_scm_scopes");

            migrationBuilder.DropTable(
                name: "client_scm_connections");

            migrationBuilder.DropIndex(
                name: "ix_review_jobs_client_provider_review",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "code_review_platform_kind",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "external_code_review_id",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "host_base_url",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "provider",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "provider_revision_id",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "repository_owner_or_namespace",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "repository_project_path",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "review_patch_identity",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "revision_base_sha",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "revision_head_sha",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "revision_start_sha",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "code_review_platform_kind",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "comment_author_display_name",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "comment_author_external_user_id",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "comment_author_is_bot",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "comment_author_login",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "comment_published_at",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "external_code_review_id",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "host_base_url",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "provider",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "repository_owner_or_namespace",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "repository_project_path",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "thread_file_path",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "thread_line_number",
                table: "mention_reply_jobs");
        }
    }
}
