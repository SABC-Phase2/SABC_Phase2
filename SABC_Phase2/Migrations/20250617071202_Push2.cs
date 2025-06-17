using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SABC_Phase2.Migrations
{
    /// <inheritdoc />
    public partial class Push2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenderAdminsDraftDocuments");

            migrationBuilder.DropTable(
                name: "TenderAdminsDraft");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenderAdminsDraft",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ClosingDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosingTime = table.Column<TimeSpan>(type: "time", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenderNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenderType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenderAdminsDraft", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenderAdminsDraftDocuments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenderDraftId = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FilePath = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenderAdminsDraftDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenderAdminsDraftDocuments_TenderAdminsDraft_TenderDraftId",
                        column: x => x.TenderDraftId,
                        principalTable: "TenderAdminsDraft",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenderAdminsDraftDocuments_TenderDraftId",
                table: "TenderAdminsDraftDocuments",
                column: "TenderDraftId");
        }
    }
}
