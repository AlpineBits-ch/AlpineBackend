using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Messaging.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationIconAndGroupEditMessageTypes : Migration
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
                .Annotation("Npgsql:Enum:message_type", "call_ended,call_missed,group_icon_changed,group_name_changed,guild_member_join,guild_member_leave,invite,message,voice_channel_invite")
                .Annotation("Npgsql:Enum:mls_generation_state", "active,terminated")
                .Annotation("Npgsql:Enum:mls_join_request_state", "cancelled,denied,fulfilled,pending")
                .OldAnnotation("Npgsql:Enum:attachment_state", "complete,pending")
                .OldAnnotation("Npgsql:Enum:author_id_type", "bot,user,webhook")
                .OldAnnotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain")
                .OldAnnotation("Npgsql:Enum:message_encryption_state", "encrypted,plain")
                .OldAnnotation("Npgsql:Enum:message_part_type", "bold,inline_code,italic,link,plain,strikethrough,underline")
                .OldAnnotation("Npgsql:Enum:message_type", "call_ended,call_missed,guild_member_join,guild_member_leave,invite,message,voice_channel_invite")
                .OldAnnotation("Npgsql:Enum:mls_generation_state", "active,terminated")
                .OldAnnotation("Npgsql:Enum:mls_join_request_state", "cancelled,denied,fulfilled,pending");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "icon_updated_at",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "icon_updated_at",
                table: "conversations");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:attachment_state", "complete,pending")
                .Annotation("Npgsql:Enum:author_id_type", "bot,user,webhook")
                .Annotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain")
                .Annotation("Npgsql:Enum:message_encryption_state", "encrypted,plain")
                .Annotation("Npgsql:Enum:message_part_type", "bold,inline_code,italic,link,plain,strikethrough,underline")
                .Annotation("Npgsql:Enum:message_type", "call_ended,call_missed,guild_member_join,guild_member_leave,invite,message,voice_channel_invite")
                .Annotation("Npgsql:Enum:mls_generation_state", "active,terminated")
                .Annotation("Npgsql:Enum:mls_join_request_state", "cancelled,denied,fulfilled,pending")
                .OldAnnotation("Npgsql:Enum:attachment_state", "complete,pending")
                .OldAnnotation("Npgsql:Enum:author_id_type", "bot,user,webhook")
                .OldAnnotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain")
                .OldAnnotation("Npgsql:Enum:message_encryption_state", "encrypted,plain")
                .OldAnnotation("Npgsql:Enum:message_part_type", "bold,inline_code,italic,link,plain,strikethrough,underline")
                .OldAnnotation("Npgsql:Enum:message_type", "call_ended,call_missed,group_icon_changed,group_name_changed,guild_member_join,guild_member_leave,invite,message,voice_channel_invite")
                .OldAnnotation("Npgsql:Enum:mls_generation_state", "active,terminated")
                .OldAnnotation("Npgsql:Enum:mls_join_request_state", "cancelled,denied,fulfilled,pending");
        }
    }
}
