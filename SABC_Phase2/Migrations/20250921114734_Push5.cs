using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SABC_Phase2.Migrations
{
    /// <inheritdoc />
    public partial class Push5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastOtpRequestTime",
                table: "Administrators",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OtpCode",
                table: "Administrators",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OtpExpiration",
                table: "Administrators",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OtpType",
                table: "Administrators",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingEmail",
                table: "Administrators",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastOtpRequestTime",
                table: "Administrators");

            migrationBuilder.DropColumn(
                name: "OtpCode",
                table: "Administrators");

            migrationBuilder.DropColumn(
                name: "OtpExpiration",
                table: "Administrators");

            migrationBuilder.DropColumn(
                name: "OtpType",
                table: "Administrators");

            migrationBuilder.DropColumn(
                name: "PendingEmail",
                table: "Administrators");
        }
    }
}
