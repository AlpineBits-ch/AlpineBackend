using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <summary>
    /// The <c>Partial</c> half of T1-7: which services' sections are absent from an assembled archive.
    ///
    /// <para><b>Purely additive.</b> One nullable-by-default column on one table. Nothing existing is
    /// altered, narrowed or dropped, and - as with <c>20260804074656_AddDataExportRequests</c>, which
    /// this follows - <b>no database enum type is touched</b>. The new <c>DataExportStatus.Partial</c>
    /// member needs no schema change at all, because <c>status</c> is a <c>character varying(32)</c>
    /// holding the member's name; that was the reason for the string column and this migration is the
    /// first time it pays. So there is no <c>AlterDatabase</c> annotation block here for a
    /// concurrently-authored migration in another service to collide with.</para>
    ///
    /// <para><b>The default matters.</b> <c>text[] NOT NULL</c> added to a table that already has rows
    /// fails without one, and this table has rows in every environment where the previous migration
    /// has run. Existing exports get an empty array, which is exactly right: every row written before
    /// this migration was resolved as <c>Ready</c> or <c>Failed</c>, and neither has missing services
    /// to name.</para>
    ///
    /// <para><b>The rollback loses which sections a partial export was missing</b> - the rows survive
    /// and keep their <c>failure_reason</c> sentence, which names the same services in prose, but the
    /// machine-readable list is gone. The <c>status</c> values themselves survive a rollback as the
    /// strings they are, so a rolled-back deployment will read <c>Partial</c> out of the column and
    /// fail to parse it into the older enum. Roll the code back with it.</para>
    /// </summary>
    public partial class AddDataExportMissingServices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "missing_services",
                table: "data_export_requests",
                type: "text[]",
                nullable: false,
                defaultValue: new List<string>());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "missing_services",
                table: "data_export_requests");
        }
    }
}
