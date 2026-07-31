using System;
using Messaging.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Messaging.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMlsJoinRequests : Migration
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
                .Annotation("Npgsql:Enum:mls_generation_state", "active,terminated")
                .Annotation("Npgsql:Enum:mls_join_request_state", "cancelled,denied,fulfilled,pending")
                .OldAnnotation("Npgsql:Enum:attachment_state", "complete,pending")
                .OldAnnotation("Npgsql:Enum:author_id_type", "bot,user,webhook")
                .OldAnnotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain")
                .OldAnnotation("Npgsql:Enum:message_encryption_state", "encrypted,plain")
                .OldAnnotation("Npgsql:Enum:message_part_type", "bold,inline_code,italic,link,plain,strikethrough,underline")
                .OldAnnotation("Npgsql:Enum:message_type", "guild_member_join,guild_member_leave,invite,message")
                .OldAnnotation("Npgsql:Enum:mls_generation_state", "active,terminated");

            migrationBuilder.CreateTable(
                name: "mls_join_requests",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    context_id = table.Column<string>(type: "text", nullable: false),
                    conversation_id = table.Column<string>(type: "text", nullable: true),
                    channel_id = table.Column<string>(type: "text", nullable: true),
                    generation = table.Column<int>(type: "integer", nullable: false),
                    requester_user_id = table.Column<string>(type: "text", nullable: false),
                    requester_device_id = table.Column<string>(type: "text", nullable: false),
                    key_package = table.Column<byte[]>(type: "bytea", nullable: false),
                    key_package_hash = table.Column<string>(type: "text", nullable: false),
                    signature_key_fingerprint = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<MlsJoinRequestState>(type: "mls_join_request_state", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fulfilled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    denied_by_user_id = table.Column<string>(type: "text", nullable: true),
                    denied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mls_join_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_mls_join_requests_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mls_join_request_approvals",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    join_request_id = table.Column<string>(type: "text", nullable: false),
                    approver_user_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mls_join_request_approvals", x => x.id);
                    table.ForeignKey(
                        name: "fk_mls_join_request_approvals_mls_join_requests_join_request_id",
                        column: x => x.join_request_id,
                        principalTable: "mls_join_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mls_join_request_approvals_join_request_id_approver_user_id",
                table: "mls_join_request_approvals",
                columns: new[] { "join_request_id", "approver_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mls_join_requests_context_id_generation_requester_device_id",
                table: "mls_join_requests",
                columns: new[] { "context_id", "generation", "requester_device_id" },
                unique: true,
                filter: "state = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_mls_join_requests_context_id_generation_state",
                table: "mls_join_requests",
                columns: new[] { "context_id", "generation", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_mls_join_requests_conversation_id",
                table: "mls_join_requests",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "ix_mls_join_requests_requester_user_id",
                table: "mls_join_requests",
                column: "requester_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mls_join_request_approvals");

            migrationBuilder.DropTable(
                name: "mls_join_requests");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:attachment_state", "complete,pending")
                .Annotation("Npgsql:Enum:author_id_type", "bot,user,webhook")
                .Annotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain")
                .Annotation("Npgsql:Enum:message_encryption_state", "encrypted,plain")
                .Annotation("Npgsql:Enum:message_part_type", "bold,inline_code,italic,link,plain,strikethrough,underline")
                .Annotation("Npgsql:Enum:message_type", "guild_member_join,guild_member_leave,invite,message")
                .Annotation("Npgsql:Enum:mls_generation_state", "active,terminated")
                .OldAnnotation("Npgsql:Enum:attachment_state", "complete,pending")
                .OldAnnotation("Npgsql:Enum:author_id_type", "bot,user,webhook")
                .OldAnnotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain")
                .OldAnnotation("Npgsql:Enum:message_encryption_state", "encrypted,plain")
                .OldAnnotation("Npgsql:Enum:message_part_type", "bold,inline_code,italic,link,plain,strikethrough,underline")
                .OldAnnotation("Npgsql:Enum:message_type", "guild_member_join,guild_member_leave,invite,message")
                .OldAnnotation("Npgsql:Enum:mls_generation_state", "active,terminated")
                .OldAnnotation("Npgsql:Enum:mls_join_request_state", "cancelled,denied,fulfilled,pending");
        }
    }
}
