using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Social.Domain.Enums;

#nullable disable

namespace Social.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserOnlineStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:online_status", "hidden,offline,online")
                .Annotation("Npgsql:Enum:relationship_status", "blocked,friends,none,pending_incoming,pending_outgoing")
                .OldAnnotation("Npgsql:Enum:relationship_status", "blocked,friends,none,pending_incoming,pending_outgoing");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_seen_at",
                table: "profiles",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<OnlineStatus>(
                name: "online_status",
                table: "profiles",
                type: "online_status",
                nullable: false,
                defaultValue: OnlineStatus.Offline);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_seen_at",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "online_status",
                table: "profiles");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:relationship_status", "blocked,friends,none,pending_incoming,pending_outgoing")
                .OldAnnotation("Npgsql:Enum:online_status", "hidden,offline,online")
                .OldAnnotation("Npgsql:Enum:relationship_status", "blocked,friends,none,pending_incoming,pending_outgoing");
        }
    }
}
