using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <summary>
    ///     Replaces the reasoning effort enum with reasoning level names. A stored None becomes no level, Low, Medium and
    ///     High become low, medium and high, a level on a pass that names a logical model is dropped because no review
    ///     read it, and "Accepts Reasoning Effort None" on a configured model becomes the model's default level none, with
    ///     none on its list of levels when the model has one.
    /// </summary>
    public partial class ReasoningLevelNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "baseline_reasoning_level",
                table: "clients",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reasoning_level",
                table: "client_review_passes",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reasoning_level",
                table: "client_purpose_logical_models",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reasoning_level",
                table: "ai_logical_models",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reasoning_level",
                table: "ai_logical_model_overrides",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "default_reasoning_level",
                table: "ai_configured_models",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE clients
                SET baseline_reasoning_level = CASE baseline_reasoning_effort WHEN 1 THEN 'low' WHEN 2 THEN 'medium' WHEN 3 THEN 'high' END;

                UPDATE ai_logical_models
                SET reasoning_level = CASE reasoning_effort WHEN 1 THEN 'low' WHEN 2 THEN 'medium' WHEN 3 THEN 'high' END;

                UPDATE ai_logical_model_overrides
                SET reasoning_level = CASE reasoning_effort WHEN 1 THEN 'low' WHEN 2 THEN 'medium' WHEN 3 THEN 'high' END;

                UPDATE client_review_passes
                SET reasoning_level = CASE
                    WHEN logical_model_name IS NOT NULL THEN NULL
                    WHEN reasoning_effort = 1 THEN 'low'
                    WHEN reasoning_effort = 2 THEN 'medium'
                    WHEN reasoning_effort = 3 THEN 'high'
                END;

                UPDATE ai_configured_models
                SET default_reasoning_level = 'none',
                    reasoning_levels = CASE
                        WHEN reasoning_levels IS NULL OR 'none' = ANY (reasoning_levels) THEN reasoning_levels
                        ELSE array_prepend('none'::text, reasoning_levels)
                    END
                WHERE supports_reasoning_effort_none;
                """);

            migrationBuilder.DropColumn(
                name: "baseline_reasoning_effort",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "reasoning_effort",
                table: "client_review_passes");

            migrationBuilder.DropColumn(
                name: "reasoning_effort",
                table: "ai_logical_models");

            migrationBuilder.DropColumn(
                name: "reasoning_effort",
                table: "ai_logical_model_overrides");

            migrationBuilder.DropColumn(
                name: "supports_reasoning_effort_none",
                table: "ai_configured_models");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "baseline_reasoning_effort",
                table: "clients",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "reasoning_effort",
                table: "client_review_passes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "reasoning_effort",
                table: "ai_logical_models",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "reasoning_effort",
                table: "ai_logical_model_overrides",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "supports_reasoning_effort_none",
                table: "ai_configured_models",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Levels above high become High and minimal becomes Low, the nearest values the enum has.
            migrationBuilder.Sql(
                """
                UPDATE clients
                SET baseline_reasoning_effort = CASE baseline_reasoning_level
                    WHEN 'minimal' THEN 1 WHEN 'low' THEN 1 WHEN 'medium' THEN 2 WHEN 'high' THEN 3 WHEN 'xhigh' THEN 3 WHEN 'max' THEN 3 ELSE 0 END;

                UPDATE ai_logical_models
                SET reasoning_effort = CASE reasoning_level
                    WHEN 'minimal' THEN 1 WHEN 'low' THEN 1 WHEN 'medium' THEN 2 WHEN 'high' THEN 3 WHEN 'xhigh' THEN 3 WHEN 'max' THEN 3 ELSE 0 END;

                UPDATE ai_logical_model_overrides
                SET reasoning_effort = CASE reasoning_level
                    WHEN 'minimal' THEN 1 WHEN 'low' THEN 1 WHEN 'medium' THEN 2 WHEN 'high' THEN 3 WHEN 'xhigh' THEN 3 WHEN 'max' THEN 3 ELSE 0 END;

                UPDATE client_review_passes
                SET reasoning_effort = CASE reasoning_level
                    WHEN 'minimal' THEN 1 WHEN 'low' THEN 1 WHEN 'medium' THEN 2 WHEN 'high' THEN 3 WHEN 'xhigh' THEN 3 WHEN 'max' THEN 3 END;

                UPDATE ai_configured_models
                SET supports_reasoning_effort_none = default_reasoning_level IS NOT DISTINCT FROM 'none';
                """);

            migrationBuilder.DropColumn(
                name: "baseline_reasoning_level",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "reasoning_level",
                table: "client_review_passes");

            migrationBuilder.DropColumn(
                name: "reasoning_level",
                table: "client_purpose_logical_models");

            migrationBuilder.DropColumn(
                name: "reasoning_level",
                table: "ai_logical_models");

            migrationBuilder.DropColumn(
                name: "reasoning_level",
                table: "ai_logical_model_overrides");

            migrationBuilder.DropColumn(
                name: "default_reasoning_level",
                table: "ai_configured_models");
        }
    }
}
