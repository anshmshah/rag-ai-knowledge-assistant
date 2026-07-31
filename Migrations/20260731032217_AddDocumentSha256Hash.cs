using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalRagAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentSha256Hash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Sha256Hash",
                table: "Documents",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("UPDATE \"Documents\" SET \"Sha256Hash\" = gen_random_uuid()::text WHERE \"Sha256Hash\" = '';");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_UserId_Sha256Hash",
                table: "Documents",
                columns: new[] { "UserId", "Sha256Hash" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Documents_UserId_Sha256Hash",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "Sha256Hash",
                table: "Documents");
        }
    }
}
