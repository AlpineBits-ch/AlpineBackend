using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Messaging.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageSearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "message_search_entries",
                columns: table => new
                {
                    message_id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: true),
                    conversation_id = table.Column<string>(type: "text", nullable: true),
                    author_id = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false)
                        .Annotation("Npgsql:TsVectorConfig", "english")
                        .Annotation("Npgsql:TsVectorProperties", new[] { "content" })
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_message_search_entries", x => x.message_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_message_search_entries_channel_id",
                table: "message_search_entries",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_message_search_entries_conversation_id",
                table: "message_search_entries",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "ix_message_search_entries_search_vector",
                table: "message_search_entries",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_search_entries");
        }
    }
}
