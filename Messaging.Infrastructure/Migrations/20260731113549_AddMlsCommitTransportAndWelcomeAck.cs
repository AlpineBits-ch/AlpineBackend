using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Messaging.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMlsCommitTransportAndWelcomeAck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "conversation_id",
                table: "pending_welcomes",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "channel_id",
                table: "pending_welcomes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "consumed_at",
                table: "pending_welcomes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "context_id",
                table: "pending_welcomes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "epoch",
                table: "pending_welcomes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "mls_commits",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    context_id = table.Column<string>(type: "text", nullable: false),
                    conversation_id = table.Column<string>(type: "text", nullable: true),
                    channel_id = table.Column<string>(type: "text", nullable: true),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    commit = table.Column<byte[]>(type: "bytea", nullable: false),
                    sender_user_id = table.Column<string>(type: "text", nullable: false),
                    sender_device_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mls_commits", x => x.id);
                    table.ForeignKey(
                        name: "fk_mls_commits_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pending_welcomes_context_id",
                table: "pending_welcomes",
                column: "context_id");

            migrationBuilder.CreateIndex(
                name: "ix_pending_welcomes_user_id_device_id_consumed_at",
                table: "pending_welcomes",
                columns: new[] { "user_id", "device_id", "consumed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_mls_commits_context_id_epoch",
                table: "mls_commits",
                columns: new[] { "context_id", "epoch" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mls_commits_conversation_id",
                table: "mls_commits",
                column: "conversation_id");

            // Existing rows would otherwise take context_id's "" default and never match a lookup,
            // stranding every Welcome that is in flight across the deploy.
            migrationBuilder.Sql(
                "UPDATE pending_welcomes SET context_id = conversation_id WHERE context_id = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mls_commits");

            migrationBuilder.DropIndex(
                name: "ix_pending_welcomes_context_id",
                table: "pending_welcomes");

            migrationBuilder.DropIndex(
                name: "ix_pending_welcomes_user_id_device_id_consumed_at",
                table: "pending_welcomes");

            migrationBuilder.DropColumn(
                name: "channel_id",
                table: "pending_welcomes");

            migrationBuilder.DropColumn(
                name: "consumed_at",
                table: "pending_welcomes");

            migrationBuilder.DropColumn(
                name: "context_id",
                table: "pending_welcomes");

            migrationBuilder.DropColumn(
                name: "epoch",
                table: "pending_welcomes");

            migrationBuilder.AlterColumn<string>(
                name: "conversation_id",
                table: "pending_welcomes",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
