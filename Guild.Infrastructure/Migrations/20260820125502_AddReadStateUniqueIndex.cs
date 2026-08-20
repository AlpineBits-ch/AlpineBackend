using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <summary>
    /// One read state per (member, channel). Stacked rows read as unread forever: the unread query
    /// left-joins every one of them, and an ack only ever moves the first.
    /// </summary>
    public partial class AddReadStateUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rows written before the pair was unique. The index cannot build over them.
            migrationBuilder.Sql(ReadStateUniquenessRepair.DeduplicateReadStatesSql);

            migrationBuilder.DropIndex(
                name: "ix_read_states_member_id",
                table: "read_states");

            migrationBuilder.CreateIndex(
                name: "ix_read_states_member_id_channel_id",
                table: "read_states",
                columns: new[] { "member_id", "channel_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_read_states_member_id_channel_id",
                table: "read_states");

            migrationBuilder.CreateIndex(
                name: "ix_read_states_member_id",
                table: "read_states",
                column: "member_id");
        }
    }
}
