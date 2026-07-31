using System;
using Messaging.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Messaging.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMlsGenerations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_mls_commits_context_id_epoch",
                table: "mls_commits");

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
                .OldAnnotation("Npgsql:Enum:message_type", "guild_member_join,guild_member_leave,invite,message");

            migrationBuilder.AddColumn<int>(
                name: "generation",
                table: "pending_welcomes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "generation",
                table: "mls_commits",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "mls_generation",
                table: "messages",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "mls_group_generations",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    context_id = table.Column<string>(type: "text", nullable: false),
                    conversation_id = table.Column<string>(type: "text", nullable: true),
                    channel_id = table.Column<string>(type: "text", nullable: true),
                    generation = table.Column<int>(type: "integer", nullable: false),
                    mls_group_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    mls_group_info = table.Column<byte[]>(type: "bytea", nullable: true),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<MlsGenerationState>(type: "mls_generation_state", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    activated_by_user_id = table.Column<string>(type: "text", nullable: false),
                    terminated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    terminated_by_user_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mls_group_generations", x => x.id);
                    table.ForeignKey(
                        name: "fk_mls_group_generations_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mls_commits_context_id_generation_epoch",
                table: "mls_commits",
                columns: new[] { "context_id", "generation", "epoch" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mls_group_generations_context_id",
                table: "mls_group_generations",
                column: "context_id",
                unique: true,
                filter: "state = 'active'");

            migrationBuilder.CreateIndex(
                name: "ix_mls_group_generations_context_id_generation",
                table: "mls_group_generations",
                columns: new[] { "context_id", "generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mls_group_generations_conversation_id",
                table: "mls_group_generations",
                column: "conversation_id");

            // ── Backfill ────────────────────────────────────────────────────────────────────────
            // Every conversation that is already encrypted has exactly one MLS group, and it has to
            // become generation 1 - otherwise the send path finds no active generation, concludes
            // the context is plaintext, and starts refusing the ciphertext its clients are
            // correctly still producing.
            migrationBuilder.Sql(@"
                INSERT INTO mls_group_generations
                    (id, context_id, conversation_id, channel_id, generation, mls_group_id,
                     mls_group_info, epoch, state, activated_at, activated_by_user_id,
                     created_at, updated_at)
                SELECT
                    'mlsg_' || upper(replace(gen_random_uuid()::text, '-', '')),
                    c.id, c.id, NULL, 1, c.mls_group_id,
                    c.mls_group_info, COALESCE(c.mls_epoch, 0), 'active', c.created_at, '',
                    c.created_at, c.updated_at
                FROM conversations c
                WHERE c.encryption_state = 'encrypted' AND c.mls_group_id IS NOT NULL;");

            // Existing commits and Welcomes all belong to that first generation; they defaulted to
            // 0, which would place them in a generation no row describes.
            migrationBuilder.Sql("UPDATE mls_commits SET generation = 1 WHERE generation = 0;");
            migrationBuilder.Sql("UPDATE pending_welcomes SET generation = 1 WHERE generation = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mls_group_generations");

            migrationBuilder.DropIndex(
                name: "ix_mls_commits_context_id_generation_epoch",
                table: "mls_commits");

            migrationBuilder.DropColumn(
                name: "generation",
                table: "pending_welcomes");

            migrationBuilder.DropColumn(
                name: "generation",
                table: "mls_commits");

            migrationBuilder.DropColumn(
                name: "mls_generation",
                table: "messages");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:attachment_state", "complete,pending")
                .Annotation("Npgsql:Enum:author_id_type", "bot,user,webhook")
                .Annotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain")
                .Annotation("Npgsql:Enum:message_encryption_state", "encrypted,plain")
                .Annotation("Npgsql:Enum:message_part_type", "bold,inline_code,italic,link,plain,strikethrough,underline")
                .Annotation("Npgsql:Enum:message_type", "guild_member_join,guild_member_leave,invite,message")
                .OldAnnotation("Npgsql:Enum:attachment_state", "complete,pending")
                .OldAnnotation("Npgsql:Enum:author_id_type", "bot,user,webhook")
                .OldAnnotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain")
                .OldAnnotation("Npgsql:Enum:message_encryption_state", "encrypted,plain")
                .OldAnnotation("Npgsql:Enum:message_part_type", "bold,inline_code,italic,link,plain,strikethrough,underline")
                .OldAnnotation("Npgsql:Enum:message_type", "guild_member_join,guild_member_leave,invite,message")
                .OldAnnotation("Npgsql:Enum:mls_generation_state", "active,terminated");

            migrationBuilder.CreateIndex(
                name: "ix_mls_commits_context_id_epoch",
                table: "mls_commits",
                columns: new[] { "context_id", "epoch" },
                unique: true);
        }
    }
}
