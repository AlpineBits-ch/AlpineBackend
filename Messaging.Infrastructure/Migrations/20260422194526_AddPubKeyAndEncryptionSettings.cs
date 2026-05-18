using Messaging.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Messaging.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPubKeyAndEncryptionSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain");

            migrationBuilder.AddColumn<byte[]>(
                name: "public_key",
                table: "members",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<ChannelEncryptionState>(
                name: "encryption_state",
                table: "conversations",
                type: "channel_encryption_state",
                nullable: false,
                defaultValue: ChannelEncryptionState.Plain);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "public_key",
                table: "members");

            migrationBuilder.DropColumn(
                name: "encryption_state",
                table: "conversations");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:Enum:channel_encryption_state", "encrypted,plain");
        }
    }
}
