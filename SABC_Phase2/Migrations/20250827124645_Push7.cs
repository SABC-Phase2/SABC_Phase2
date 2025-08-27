using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SABC_Phase2.Migrations
{
    /// <inheritdoc />
    public partial class Push7 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OriginalCountryCode",
                table: "Users",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalEmail",
                table: "Users",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalPhoneNumber",
                table: "Users",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OriginalCountryCode",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "OriginalEmail",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "OriginalPhoneNumber",
                table: "Users");
        }
    }
}
