using System;
using Messaging.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Messaging.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAttachmentEndpoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:attachment_state", "complete,pending")
                .Annotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain")
                .Annotation("Npgsql:Enum:message_type", "message")
                .OldAnnotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain");

            migrationBuilder.CreateTable(
                name: "attachments",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    thumbnail_url = table.Column<string>(type: "text", nullable: true),
                    creator_id = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<AttachmentState>(type: "attachment_state", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attachments", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attachments");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain")
                .OldAnnotation("Npgsql:Enum:attachment_state", "complete,pending")
                .OldAnnotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain")
                .OldAnnotation("Npgsql:Enum:message_type", "message");
        }
    }
}
