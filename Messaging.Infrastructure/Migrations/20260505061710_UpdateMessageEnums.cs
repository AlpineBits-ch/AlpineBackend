using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Messaging.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UpdateMessageEnums : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:attachment_state", "complete,pending")
                .Annotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain")
                .Annotation("Npgsql:Enum:message_type", "guild_member_join,guild_member_leave,invite,message")
                .OldAnnotation("Npgsql:Enum:attachment_state", "complete,pending")
                .OldAnnotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain")
                .OldAnnotation("Npgsql:Enum:message_type", "invite,message");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:attachment_state", "complete,pending")
                .Annotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain")
                .Annotation("Npgsql:Enum:message_type", "invite,message")
                .OldAnnotation("Npgsql:Enum:attachment_state", "complete,pending")
                .OldAnnotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain")
                .OldAnnotation("Npgsql:Enum:message_type", "guild_member_join,guild_member_leave,invite,message");
        }
    }
}
