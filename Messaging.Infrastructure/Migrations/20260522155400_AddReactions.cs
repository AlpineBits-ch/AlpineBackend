using System;
using System.Collections.Generic;
using Messaging.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Messaging.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:attachment_state", "complete,pending")
                .Annotation("Npgsql:Enum:author_id_type", "bot,user,webhook")
                .Annotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain")
                .Annotation("Npgsql:Enum:message_encryption_state", "encrypted,plain")
                .Annotation("Npgsql:Enum:message_part_type", "bold,inline_code,italic,link,plain,strikethrough,underline")
                .Annotation("Npgsql:Enum:message_type", "guild_member_join,guild_member_leave,invite,message")
                .OldAnnotation("Npgsql:Enum:attachment_state", "complete,pending")
                .OldAnnotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain")
                .OldAnnotation("Npgsql:Enum:message_type", "guild_member_join,guild_member_leave,invite,message");

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    author_id = table.Column<string>(type: "text", nullable: false),
                    context_id = table.Column<string>(type: "text", nullable: false),
                    encryption_state = table.Column<MessageEncryptionState>(type: "message_encryption_state", nullable: false),
                    mls_epoch = table.Column<long>(type: "bigint", nullable: true),
                    mls_sequence_number = table.Column<long>(type: "bigint", nullable: true),
                    sender_device_id = table.Column<string>(type: "text", nullable: true),
                    channel_id = table.Column<string>(type: "text", nullable: true),
                    conversation_id = table.Column<string>(type: "text", nullable: true),
                    in_reply_to = table.Column<string>(type: "text", nullable: true),
                    type = table.Column<MessageType>(type: "message_type", nullable: false),
                    author_id_type = table.Column<AuthorIdType>(type: "author_id_type", nullable: false),
                    mentions = table.Column<List<string>>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "minimal_attachments",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    thumbnail_url = table.Column<string>(type: "text", nullable: true),
                    thumbnail_id = table.Column<string>(type: "text", nullable: true),
                    message_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_minimal_attachments", x => x.id);
                    table.ForeignKey(
                        name: "fk_minimal_attachments_messages_message_id",
                        column: x => x.message_id,
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reactions",
                columns: table => new
                {
                    message_id = table.Column<string>(type: "text", nullable: false),
                    emoji = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    context_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    conversation_id = table.Column<string>(type: "text", nullable: true),
                    channel_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reactions", x => new { x.message_id, x.user_id, x.emoji });
                    table.ForeignKey(
                        name: "fk_reactions_messages_message_id",
                        column: x => x.message_id,
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_messages_author_id",
                table: "messages",
                column: "author_id");

            migrationBuilder.CreateIndex(
                name: "ix_messages_channel_id",
                table: "messages",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_messages_context_id",
                table: "messages",
                column: "context_id");

            migrationBuilder.CreateIndex(
                name: "ix_messages_conversation_id",
                table: "messages",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "ix_minimal_attachments_message_id",
                table: "minimal_attachments",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "ix_reactions_message_id",
                table: "reactions",
                column: "message_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "minimal_attachments");

            migrationBuilder.DropTable(
                name: "reactions");

            migrationBuilder.DropTable(
                name: "messages");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:attachment_state", "complete,pending")
                .Annotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain")
                .Annotation("Npgsql:Enum:message_type", "guild_member_join,guild_member_leave,invite,message")
                .OldAnnotation("Npgsql:Enum:attachment_state", "complete,pending")
                .OldAnnotation("Npgsql:Enum:author_id_type", "bot,user,webhook")
                .OldAnnotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain")
                .OldAnnotation("Npgsql:Enum:message_encryption_state", "encrypted,plain")
                .OldAnnotation("Npgsql:Enum:message_part_type", "bold,inline_code,italic,link,plain,strikethrough,underline")
                .OldAnnotation("Npgsql:Enum:message_type", "guild_member_join,guild_member_leave,invite,message");
        }
    }
}
