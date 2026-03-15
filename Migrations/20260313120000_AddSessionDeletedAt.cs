using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalRagAPI.Migrations
{
    public partial class AddSessionDeletedAt : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "ChatSessions",
                type: "timestamp with time zone",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "ChatSessions");
        }
    }
}
