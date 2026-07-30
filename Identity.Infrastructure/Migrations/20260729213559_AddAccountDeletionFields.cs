using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountDeletionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:age_vertification_level", "ai_estimation,goverment_id,none,self_declaration")
                .Annotation("Npgsql:Enum:device_status", "active,removed")
                .Annotation("Npgsql:Enum:device_type", "desktop,mobile,web")
                .Annotation("Npgsql:Enum:direct_message_settings", "allow_all,filter_all,filter_non_friends")
                .Annotation("Npgsql:Enum:privacy_settings", "allow_data_collection,allow_data_use_for_personalization,allow_voice_recorded_in_clips,none")
                .Annotation("Npgsql:Enum:theme", "dark,light,midnight,system")
                .Annotation("Npgsql:Enum:user_status", "active,banned,deleted,inactive,pending_deletion,purge_in_progress")
                .Annotation("Npgsql:Enum:user_type", "admin,bot,default,moderator")
                .OldAnnotation("Npgsql:Enum:age_vertification_level", "ai_estimation,goverment_id,none,self_declaration")
                .OldAnnotation("Npgsql:Enum:device_status", "active,removed")
                .OldAnnotation("Npgsql:Enum:device_type", "desktop,mobile,web")
                .OldAnnotation("Npgsql:Enum:direct_message_settings", "allow_all,filter_all,filter_non_friends")
                .OldAnnotation("Npgsql:Enum:privacy_settings", "allow_data_collection,allow_data_use_for_personalization,allow_voice_recorded_in_clips,none")
                .OldAnnotation("Npgsql:Enum:theme", "dark,light,midnight,system")
                .OldAnnotation("Npgsql:Enum:user_status", "active,banned,inactive")
                .OldAnnotation("Npgsql:Enum:user_type", "admin,bot,default,moderator");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deletion_requested_at",
                table: "asp_net_users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "purge_scheduled_at",
                table: "asp_net_users",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "deletion_requested_at",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "purge_scheduled_at",
                table: "asp_net_users");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:age_vertification_level", "ai_estimation,goverment_id,none,self_declaration")
                .Annotation("Npgsql:Enum:device_status", "active,removed")
                .Annotation("Npgsql:Enum:device_type", "desktop,mobile,web")
                .Annotation("Npgsql:Enum:direct_message_settings", "allow_all,filter_all,filter_non_friends")
                .Annotation("Npgsql:Enum:privacy_settings", "allow_data_collection,allow_data_use_for_personalization,allow_voice_recorded_in_clips,none")
                .Annotation("Npgsql:Enum:theme", "dark,light,midnight,system")
                .Annotation("Npgsql:Enum:user_status", "active,banned,inactive")
                .Annotation("Npgsql:Enum:user_type", "admin,bot,default,moderator")
                .OldAnnotation("Npgsql:Enum:age_vertification_level", "ai_estimation,goverment_id,none,self_declaration")
                .OldAnnotation("Npgsql:Enum:device_status", "active,removed")
                .OldAnnotation("Npgsql:Enum:device_type", "desktop,mobile,web")
                .OldAnnotation("Npgsql:Enum:direct_message_settings", "allow_all,filter_all,filter_non_friends")
                .OldAnnotation("Npgsql:Enum:privacy_settings", "allow_data_collection,allow_data_use_for_personalization,allow_voice_recorded_in_clips,none")
                .OldAnnotation("Npgsql:Enum:theme", "dark,light,midnight,system")
                .OldAnnotation("Npgsql:Enum:user_status", "active,banned,deleted,inactive,pending_deletion,purge_in_progress")
                .OldAnnotation("Npgsql:Enum:user_type", "admin,bot,default,moderator");
        }
    }
}
