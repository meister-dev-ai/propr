// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveClientKeyFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_clients_key",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "allowed_scopes",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "key",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "key_expires_at",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "key_hash",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "key_rotated_at",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "previous_key_expires_at",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "previous_key_hash",
                table: "clients");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "allowed_scopes",
                table: "clients",
                type: "integer",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddColumn<string>(
                name: "key",
                table: "clients",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "key_expires_at",
                table: "clients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "key_hash",
                table: "clients",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "key_rotated_at",
                table: "clients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "previous_key_expires_at",
                table: "clients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "previous_key_hash",
                table: "clients",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_clients_key",
                table: "clients",
                column: "key",
                unique: true);
        }
    }
}
