using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Messaging.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWelcomeMessags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_welcomes",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    conversation_id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    device_id = table.Column<string>(type: "text", nullable: false),
                    welcome = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pending_welcomes", x => x.id);
                    table.ForeignKey(
                        name: "fk_pending_welcomes_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pending_welcomes_conversation_id",
                table: "pending_welcomes",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "ix_pending_welcomes_user_id",
                table: "pending_welcomes",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_welcomes");
        }
    }
}
