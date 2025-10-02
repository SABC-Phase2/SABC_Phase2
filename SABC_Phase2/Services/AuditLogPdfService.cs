using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SABC_Phase2.Models.Tender;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SABC_Phase2.Services
{
    public class AuditLogPdfService
    {
        public byte[] GenerateAuditLogReport(List<AuditLog> auditLogs, DateTime? fromDate, DateTime? toDate, string reportCode = null)
        {
            QuestPDF.Settings.License = LicenseType.Community;

            var document = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4.Landscape());
                    page.Margin(30);
                    page.DefaultTextStyle(x => x.FontSize(10));

                    page.Content().Element(content => ComposeContent(content, auditLogs, fromDate, toDate, reportCode));
                    page.Footer().AlignCenter().Text(text =>
                    {
                        text.CurrentPageNumber();
                        text.Span(" / ");
                        text.TotalPages();
                    });
                });
            });

            return document.GeneratePdf();
        }

        private void ComposeContent(IContainer container, List<AuditLog> auditLogs, DateTime? fromDate, DateTime? toDate, string reportCode)
        {
            container.Column(column =>
            {
                column.Item().PaddingBottom(20).Row(row =>
                {
                    var logoPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "lib", "Images", "cropped_logoblack.png");
                    if (File.Exists(logoPath))
                    {
                        var logoBytes = File.ReadAllBytes(logoPath);
                        row.ConstantItem(60).AlignMiddle().Element(c => c.Image(logoBytes));
                    }
                    else
                    {
                        row.ConstantItem(60);
                    }

                    row.RelativeItem().AlignMiddle().AlignCenter().Text("Audit Log Report")
                        .FontSize(24).Bold().FontColor(Colors.Black);

                    row.ConstantItem(60).AlignMiddle().AlignRight().Element(e =>
                    {
                        if (!string.IsNullOrWhiteSpace(reportCode))
                        {
                            e.Text(reportCode)
                                .FontColor(Colors.Red.Medium)
                                .FontSize(16)
                                .Bold();
                        }
                    });
                });

                column.Item().PaddingBottom(20).Text(new string('=', 130)).FontSize(10).FontFamily("Courier New");

                // --- Dynamic column widths, scaled to fit table ---
                // Calculate max lengths
                int maxUserNameLength = auditLogs.Any() ? auditLogs.Max(l => (l.AdminFullName ?? "").Length) : 5;
                int maxUserRoleLength = auditLogs.Any() ? auditLogs.Max(l => GetUserRole(l).Length) : 4;
                int maxActionLength = auditLogs.Any() ? auditLogs.Max(l => GetActionTypeDisplay(l.ActionType).Length) : 5;
                int maxDescriptionLength = auditLogs.Any() ? auditLogs.Max(l => (l.Description ?? "").Length) : 8;

                // Assign raw relative widths (minimum 2, maximum 6; scale with string length, but capped)
                float userColRaw = Math.Clamp(2f + maxUserNameLength / 10f, 2f, 6f);
                float userRoleColRaw = Math.Clamp(2f + maxUserRoleLength / 10f, 2f, 5f);
                float actionColRaw = Math.Clamp(2f + maxActionLength / 12f, 2f, 5f);
                float descriptionColRaw = Math.Clamp(3f + maxDescriptionLength / 40f, 3f, 7f);
                float timeStampColRaw = 2.5f;

                // Total relative width
                float totalRawWidth = timeStampColRaw + userColRaw + userRoleColRaw + actionColRaw + descriptionColRaw;

                // Scale so the sum is 16 (fits A4 landscape well)
                float scale = 16f / totalRawWidth;

                float timeStampCol = timeStampColRaw * scale;
                float userCol = userColRaw * scale;
                float userRoleCol = userRoleColRaw * scale;
                float actionCol = actionColRaw * scale;
                float descriptionCol = descriptionColRaw * scale;

                if (auditLogs.Any())
                {
                    column.Item().PaddingBottom(20).Column(dataColumn =>
                    {
                        dataColumn.Item().Row(headerRow =>
                        {
                            headerRow.RelativeColumn(timeStampCol).Element(InvisibleCellStyle).Text("Time Stamp").Bold().FontSize(12);
                            headerRow.RelativeColumn(userCol).Element(InvisibleCellStyle).Text("User").Bold().FontSize(12);
                            headerRow.RelativeColumn(userRoleCol).Element(InvisibleCellStyle).Text("User Role").Bold().FontSize(12);
                            headerRow.RelativeColumn(actionCol).Element(InvisibleCellStyle).Text("Action").Bold().FontSize(12);
                            headerRow.RelativeColumn(descriptionCol).Element(InvisibleCellStyle).Text("Description").Bold().FontSize(12);
                        });

                        foreach (var log in auditLogs.OrderByDescending(l => l.Timestamp))
                        {
                            dataColumn.Item().ShowEntire().Row(dataRow =>
                            {
                                dataRow.RelativeColumn(timeStampCol).Element(InvisibleCellStyle).Text(log.Timestamp.ToString("dd/MM/yyyy HH:mm:ss"));
                                dataRow.RelativeColumn(userCol).Element(InvisibleCellStyle).Text(log.AdminFullName ?? "N/A");
                                dataRow.RelativeColumn(userRoleCol).Element(InvisibleCellStyle).Text(GetUserRole(log));
                                dataRow.RelativeColumn(actionCol).Element(InvisibleCellStyle).Text(GetActionTypeDisplay(log.ActionType));
                                dataRow.RelativeColumn(descriptionCol).Element(InvisibleCellStyle).Text(log.Description ?? "N/A");
                            });
                        }
                    });
                }

                column.Item().PaddingVertical(20).Text(new string('=', 130)).FontSize(10).FontFamily("Courier New");
                column.Item().PaddingBottom(10).Text($"Date generated: {DateTime.Now:dd MMMM yyyy HH:mm}").FontSize(12).Bold();

                if (fromDate.HasValue && toDate.HasValue)
                {
                    var timeRangeText = GetTimeRangeDescription(fromDate.Value, toDate.Value);
                    column.Item().Text($"Time Range: {timeRangeText}").FontSize(12);
                }
                else if (fromDate.HasValue || toDate.HasValue)
                {
                    var dateText = fromDate?.ToString("dd MMMM yyyy") ?? toDate?.ToString("dd MMMM yyyy");
                    column.Item().Text($"Time Range: From {dateText}").FontSize(12);
                }

                if (!auditLogs.Any())
                {
                    column.Item().PaddingTop(20).AlignCenter().Text("No audit logs found for the selected period.")
                        .FontSize(12).Italic().FontColor(Colors.Grey.Darken1);
                }
            });
        }

        private IContainer InvisibleCellStyle(IContainer container)
        {
            return container.Padding(8);
        }

        private string GetUserRole(AuditLog log)
        {
            if (string.IsNullOrWhiteSpace(log.Role))
                return "User";

            return log.Role switch
            {
                "Tender_Administrator" => "Tender Administrator",
                "IT_Admin" => "IT Administrator",
                "Vendor_Administrator" => "Vendor Administrator",
                _ => log.Role
            };
        }

        private string GetTimeRangeDescription(DateTime fromDate, DateTime toDate)
        {
            var timeDifference = toDate - fromDate;

            if (timeDifference.Days == 7)
                return "Last 7 days";
            else if (timeDifference.Days == 30 || timeDifference.Days == 31)
                return "Last 30 days";
            else if (timeDifference.Days == 1)
                return "Last 24 hours";
            else
                return $"{fromDate:dd MMM yyyy} to {toDate:dd MMM yyyy}";
        }

        private string GetActionTypeDisplay(string actionType)
        {
            if (string.IsNullOrEmpty(actionType)) return "N/A";

            return actionType switch
            {
                "CreateTender" => "Create Tender",
                "EditTender" => "Edit Tender",
                "DeleteTender" => "Delete Tender",
                "PublishTender" => "Publish Tender",
                "CloseTender" => "Close Tender",
                "AwardTender" => "Award Tender",
                "ScheduleTender" => "Schedule Tender",
                "EditScheduledTender" => "Edit Scheduled Tender",
                "DeleteScheduledTender" => "Delete Scheduled Tender",
                "CreateDraft" => "Create Draft",
                "EditDraft" => "Edit Draft",
                "DeleteDraft" => "Delete Draft",
                "Create" => "Create Draft",
                "Edit" => "Edit Draft",
                "CreateUser" => "Create User",
                "EditUser" => "Edit User",
                "DeleteUser" => "Delete User",
                "BulkDeleteUsers" => "Bulk Delete Users",
                "ReactivateUser" => "Reactivate User",
                "ReacvateUser" => "Reactivate User",
                "DeactivateUser" => "Deactivate User",
                "GenerateTenderSupplierReport" => "Generate Supplier Report",
                "GenerateClosedTendersSummaryReport" => "Generate Summary Report",
                "GenerateReport" => "Generate Report",
                "Login" => "Login",
                "Logout" => "Logout",
                "PasswordReset" => "Password Reset",
                "PasswordChange" => "Password Change",
                "AccountLocked" => "Account Locked",
                "AccountUnlocked" => "Account Unlocked",
                "SystemConfiguration" => "System Configuration",
                "UpdateSettings" => "Update Settings",
                "BackupDatabase" => "Backup Database",
                "RestoreDatabase" => "Restore Database",
                "SecurityAlert" => "Security Alert",
                "UnauthorizedAccess" => "Unauthorized Access",
                "DataExport" => "Data Export",
                "DataImport" => "Data Import",
                _ => ConvertPascalCaseToReadable(actionType)
            };
        }

        private string ConvertPascalCaseToReadable(string input)
        {
            if (string.IsNullOrEmpty(input))
                return "N/A";

            var result = "";
            for (int i = 0; i < input.Length; i++)
            {
                if (i > 0 && char.IsUpper(input[i]) && !char.IsUpper(input[i - 1]))
                {
                    result += " ";
                }
                result += input[i];
            }

            return result;
        }
    }
}