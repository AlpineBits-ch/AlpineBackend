using Guild.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReAddEnum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:channel_type", "announcement,forum,text,ticket,voice")
                .Annotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .Annotation("Npgsql:Enum:invite_state", "active,expired")
                .Annotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .Annotation("Npgsql:Enum:member_type", "bot,default,persona")
                .Annotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .Annotation("Npgsql:Enum:role_type", "everyone,none")
                .Annotation("Npgsql:Enum:wiki_visibility", "private,public")
                .OldAnnotation("Npgsql:Enum:channel_type", "announcement,forum,text,ticket,voice")
                .OldAnnotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .OldAnnotation("Npgsql:Enum:invite_state", "active,expired")
                .OldAnnotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .OldAnnotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .OldAnnotation("Npgsql:Enum:role_type", "everyone,none")
                .OldAnnotation("Npgsql:Enum:wiki_visibility", "private,public");

            migrationBuilder.AddColumn<MemberType>(
                name: "type",
                table: "guild_members",
                type: "member_type",
                nullable: false,
                defaultValue: MemberType.Default);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "type",
                table: "guild_members");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:channel_type", "announcement,forum,text,ticket,voice")
                .Annotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .Annotation("Npgsql:Enum:invite_state", "active,expired")
                .Annotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .Annotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .Annotation("Npgsql:Enum:role_type", "everyone,none")
                .Annotation("Npgsql:Enum:wiki_visibility", "private,public")
                .OldAnnotation("Npgsql:Enum:channel_type", "announcement,forum,text,ticket,voice")
                .OldAnnotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .OldAnnotation("Npgsql:Enum:invite_state", "active,expired")
                .OldAnnotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .OldAnnotation("Npgsql:Enum:member_type", "bot,default,persona")
                .OldAnnotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .OldAnnotation("Npgsql:Enum:role_type", "everyone,none")
                .OldAnnotation("Npgsql:Enum:wiki_visibility", "private,public");
        }
    }
}
