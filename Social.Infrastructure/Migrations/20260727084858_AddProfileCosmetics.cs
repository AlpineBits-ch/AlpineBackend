using Microsoft.EntityFrameworkCore.Migrations;
using Social.Domain.Enums;

#nullable disable

namespace Social.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileCosmetics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:online_status", "do_not_disturb,hidden,idle,offline,online")
                .Annotation("Npgsql:Enum:profile_font", "default,display,handwritten,monospace,rounded,serif")
                .Annotation("Npgsql:Enum:relationship_status", "blocked,friends,none,pending_incoming,pending_outgoing")
                .OldAnnotation("Npgsql:Enum:online_status", "do_not_disturb,hidden,idle,offline,online")
                .OldAnnotation("Npgsql:Enum:relationship_status", "blocked,friends,none,pending_incoming,pending_outgoing");

            migrationBuilder.AddColumn<string>(
                name: "accent_color",
                table: "profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<ProfileFont>(
                name: "font",
                table: "profiles",
                type: "profile_font",
                nullable: false,
                defaultValue: ProfileFont.Default);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "accent_color",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "font",
                table: "profiles");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:online_status", "do_not_disturb,hidden,idle,offline,online")
                .Annotation("Npgsql:Enum:relationship_status", "blocked,friends,none,pending_incoming,pending_outgoing")
                .OldAnnotation("Npgsql:Enum:online_status", "do_not_disturb,hidden,idle,offline,online")
                .OldAnnotation("Npgsql:Enum:profile_font", "default,display,handwritten,monospace,rounded,serif")
                .OldAnnotation("Npgsql:Enum:relationship_status", "blocked,friends,none,pending_incoming,pending_outgoing");
        }
    }
}
