using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SABC_Phase2.Migrations
{
    /// <inheritdoc />
    public partial class Push4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Applied_For_Tenders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenderId = table.Column<int>(type: "int", nullable: false),
                    OVRS_UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Applied_For_Tenders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Applied_For_Tenders_Tenders_TenderId",
                        column: x => x.TenderId,
                        principalTable: "Tenders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Applied_For_Tenders_Users_OVRS_UserId",
                        column: x => x.OVRS_UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Applied_For_Tenders_OVRS_UserId",
                table: "Applied_For_Tenders",
                column: "OVRS_UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Applied_For_Tenders_TenderId",
                table: "Applied_For_Tenders",
                column: "TenderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Applied_For_Tenders");
        }
    }
}
