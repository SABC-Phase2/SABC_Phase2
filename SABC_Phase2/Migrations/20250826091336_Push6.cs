using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SABC_Phase2.Migrations
{
    /// <inheritdoc />
    public partial class Push6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PasswordLastUpdated",
                table: "Users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PasswordLastUpdated",
                table: "Users",
                type: "datetime2",
                nullable: true);
        }
    }
}
