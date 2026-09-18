using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JameX.Catalog.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVideosUploaderIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_videos_uploader_id_created_at",
                table: "videos",
                columns: new[] { "uploader_id", "created_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_videos_uploader_id_created_at",
                table: "videos");
        }
    }
}
