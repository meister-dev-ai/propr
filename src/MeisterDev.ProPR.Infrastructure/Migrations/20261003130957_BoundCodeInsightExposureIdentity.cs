// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BoundCodeInsightExposureIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION code_insight_exposure_identity(
                    job_id uuid, file_path text, model_id text, logical_model_name text, source_name text)
                RETURNS text
                LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
                AS $function$
                    SELECT pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
                        pg_catalog.jsonb_build_array(job_id::text, file_path, model_id, logical_model_name, source_name)::text,
                        'UTF8')), 'hex');
                $function$;
                """);
            migrationBuilder.DropIndex(
                name: "IX_code_insight_review_exposures_JobId_FilePath_ModelId_Logica~",
                table: "code_insight_review_exposures");

            migrationBuilder.AddColumn<string>(
                name: "IdentityFingerprint",
                table: "code_insight_review_exposures",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                computedColumnSql: "code_insight_exposure_identity(\"JobId\", \"FilePath\", \"ModelId\", \"LogicalModelName\", \"Source\")",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_code_insight_review_exposures_IdentityFingerprint",
                table: "code_insight_review_exposures",
                column: "IdentityFingerprint",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_code_insight_review_exposures_IdentityFingerprint",
                table: "code_insight_review_exposures");

            migrationBuilder.DropColumn(
                name: "IdentityFingerprint",
                table: "code_insight_review_exposures");

            // Retain bounded tuple uniqueness so rollback preserves accepted wide identities.
            // The expression index still depends on the immutable identity function.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX "IX_code_insight_review_exposures_JobId_FilePath_ModelId_Logica~"
                ON code_insight_review_exposures
                    (code_insight_exposure_identity("JobId", "FilePath", "ModelId", "LogicalModelName", "Source"));
                """);
        }
    }
}
