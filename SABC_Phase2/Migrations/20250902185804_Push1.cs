using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SABC_Phase2.Migrations
{
    /// <inheritdoc />
    public partial class Push1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Administrators",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Email = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Administrators", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledTenders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenderType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TenderNumber = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ClosingDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClosingTime = table.Column<TimeSpan>(type: "time", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ScheduledPublishDateTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledTenders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenderAdminsDraft",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DraftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenderType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenderNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClosingDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosingTime = table.Column<TimeSpan>(type: "time", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenderAdminsDraft", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Role = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LegacyUserId = table.Column<int>(type: "int", nullable: true),
                    OriginalEmail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OriginalPhoneNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OriginalCountryCode = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OtpCode = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OtpExpiration = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PendingPhoneNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PendingCountryCode = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PendingEmail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OtpType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AccountStatus = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledTendersDocuments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FileName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SharePointPath = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ScheduledTenderId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledTendersDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduledTendersDocuments_ScheduledTenders_ScheduledTenderId",
                        column: x => x.ScheduledTenderId,
                        principalTable: "ScheduledTenders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TenderAdminsDraftDocuments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FileName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SharePointPath = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TenderDraftId = table.Column<int>(type: "int", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "TenderApplicationDrafts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DraftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OVRS_UserId = table.Column<int>(type: "int", nullable: true),
                    TenderId = table.Column<int>(type: "int", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenderApplicationDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenderApplicationDrafts_Users_OVRS_UserId",
                        column: x => x.OVRS_UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TenderApplicationDraftDocuments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FileName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SharePointPath = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TenderApplicationDraftId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenderApplicationDraftDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenderApplicationDraftDocuments_TenderApplicationDrafts_TenderApplicationDraftId",
                        column: x => x.TenderApplicationDraftId,
                        principalTable: "TenderApplicationDrafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationDocuments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FileName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SharePointPath = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TenderApplicationId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Applied_For_Tenders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenderId = table.Column<int>(type: "int", nullable: false),
                    OVRS_UserId = table.Column<int>(type: "int", nullable: false),
                    DraftId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DateApplied = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Applied_For_Tenders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Applied_For_Tenders_Users_OVRS_UserId",
                        column: x => x.OVRS_UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AwardedTenders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AwardedCompanyName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TenderId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AwardedTenders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenderType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TenderNumber = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ClosingDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClosingTime = table.Column<TimeSpan>(type: "time", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DatePublished = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DraftId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AwardedTenderId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tenders_AwardedTenders_AwardedTenderId",
                        column: x => x.AwardedTenderId,
                        principalTable: "AwardedTenders",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TenderDocuments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FileName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SharePointPath = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TenderId = table.Column<int>(type: "int", nullable: false),
                    AwardedTenderId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenderDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenderDocuments_AwardedTenders_AwardedTenderId",
                        column: x => x.AwardedTenderId,
                        principalTable: "AwardedTenders",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TenderDocuments_Tenders_TenderId",
                        column: x => x.TenderId,
                        principalTable: "Tenders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationDocuments_TenderApplicationId",
                table: "ApplicationDocuments",
                column: "TenderApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_Applied_For_Tenders_OVRS_UserId",
                table: "Applied_For_Tenders",
                column: "OVRS_UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Applied_For_Tenders_TenderId",
                table: "Applied_For_Tenders",
                column: "TenderId");

            migrationBuilder.CreateIndex(
                name: "IX_AwardedTenders_TenderId",
                table: "AwardedTenders",
                column: "TenderId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTendersDocuments_ScheduledTenderId",
                table: "ScheduledTendersDocuments",
                column: "ScheduledTenderId");

            migrationBuilder.CreateIndex(
                name: "IX_TenderAdminsDraftDocuments_TenderDraftId",
                table: "TenderAdminsDraftDocuments",
                column: "TenderDraftId");

            migrationBuilder.CreateIndex(
                name: "IX_TenderApplicationDraftDocuments_TenderApplicationDraftId",
                table: "TenderApplicationDraftDocuments",
                column: "TenderApplicationDraftId");

            migrationBuilder.CreateIndex(
                name: "IX_TenderApplicationDrafts_OVRS_UserId",
                table: "TenderApplicationDrafts",
                column: "OVRS_UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TenderDocuments_AwardedTenderId",
                table: "TenderDocuments",
                column: "AwardedTenderId");

            migrationBuilder.CreateIndex(
                name: "IX_TenderDocuments_TenderId",
                table: "TenderDocuments",
                column: "TenderId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenders_AwardedTenderId",
                table: "Tenders",
                column: "AwardedTenderId");

            migrationBuilder.AddForeignKey(
                name: "FK_ApplicationDocuments_Applied_For_Tenders_TenderApplicationId",
                table: "ApplicationDocuments",
                column: "TenderApplicationId",
                principalTable: "Applied_For_Tenders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Applied_For_Tenders_Tenders_TenderId",
                table: "Applied_For_Tenders",
                column: "TenderId",
                principalTable: "Tenders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AwardedTenders_Tenders_TenderId",
                table: "AwardedTenders",
                column: "TenderId",
                principalTable: "Tenders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AwardedTenders_Tenders_TenderId",
                table: "AwardedTenders");

            migrationBuilder.DropTable(
                name: "Administrators");

            migrationBuilder.DropTable(
                name: "ApplicationDocuments");

            migrationBuilder.DropTable(
                name: "ScheduledTendersDocuments");

            migrationBuilder.DropTable(
                name: "TenderAdminsDraftDocuments");

            migrationBuilder.DropTable(
                name: "TenderApplicationDraftDocuments");

            migrationBuilder.DropTable(
                name: "TenderDocuments");

            migrationBuilder.DropTable(
                name: "Applied_For_Tenders");

            migrationBuilder.DropTable(
                name: "ScheduledTenders");

            migrationBuilder.DropTable(
                name: "TenderAdminsDraft");

            migrationBuilder.DropTable(
                name: "TenderApplicationDrafts");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Tenders");

            migrationBuilder.DropTable(
                name: "AwardedTenders");
        }
    }
}
