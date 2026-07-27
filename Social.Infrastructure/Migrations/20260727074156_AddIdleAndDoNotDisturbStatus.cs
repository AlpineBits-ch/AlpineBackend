using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Social.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIdleAndDoNotDisturbStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:online_status", "do_not_disturb,hidden,idle,offline,online")
                .Annotation("Npgsql:Enum:relationship_status", "blocked,friends,none,pending_incoming,pending_outgoing")
                .OldAnnotation("Npgsql:Enum:online_status", "hidden,offline,online")
                .OldAnnotation("Npgsql:Enum:relationship_status", "blocked,friends,none,pending_incoming,pending_outgoing");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:online_status", "hidden,offline,online")
                .Annotation("Npgsql:Enum:relationship_status", "blocked,friends,none,pending_incoming,pending_outgoing")
                .OldAnnotation("Npgsql:Enum:online_status", "do_not_disturb,hidden,idle,offline,online")
                .OldAnnotation("Npgsql:Enum:relationship_status", "blocked,friends,none,pending_incoming,pending_outgoing");
        }
    }
}
