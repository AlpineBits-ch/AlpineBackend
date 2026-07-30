using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Messaging.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMessagePinning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_pinned",
                table: "messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "pinned_at",
                table: "messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pinned_by_id",
                table: "messages",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_context_id_is_pinned",
                table: "messages",
                columns: new[] { "context_id", "is_pinned" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_messages_context_id_is_pinned",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "is_pinned",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "pinned_at",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "pinned_by_id",
                table: "messages");
        }
    }
}
