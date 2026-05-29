using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YtAudio.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramFileId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "telegram_file_id",
                table: "tracks",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "telegram_file_id",
                table: "tracks");
        }
    }
}
