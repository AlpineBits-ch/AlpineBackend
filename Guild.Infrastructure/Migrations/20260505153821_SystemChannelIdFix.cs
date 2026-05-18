using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SystemChannelIdFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_guilds_channels_system_channel_id1",
                table: "guilds");

            migrationBuilder.DropIndex(
                name: "ix_guilds_system_channel_id1",
                table: "guilds");

            migrationBuilder.DropColumn(
                name: "system_channel_id1",
                table: "guilds");

            migrationBuilder.CreateIndex(
                name: "ix_guilds_system_channel_id",
                table: "guilds",
                column: "system_channel_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_guilds_channels_system_channel_id",
                table: "guilds",
                column: "system_channel_id",
                principalTable: "channels",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_guilds_channels_system_channel_id",
                table: "guilds");

            migrationBuilder.DropIndex(
                name: "ix_guilds_system_channel_id",
                table: "guilds");

            migrationBuilder.AddColumn<string>(
                name: "system_channel_id1",
                table: "guilds",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_guilds_system_channel_id1",
                table: "guilds",
                column: "system_channel_id1");

            migrationBuilder.AddForeignKey(
                name: "fk_guilds_channels_system_channel_id1",
                table: "guilds",
                column: "system_channel_id1",
                principalTable: "channels",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
