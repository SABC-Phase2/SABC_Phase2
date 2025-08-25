using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SABC_Phase2.Migrations
{
    /// <inheritdoc />
    public partial class Push3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PendingCountryCode",
                table: "Users",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingPhoneNumber",
                table: "Users",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PendingCountryCode",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PendingPhoneNumber",
                table: "Users");
        }
    }
}
