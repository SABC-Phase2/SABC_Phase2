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
            // Set QuestPDF license (required for commercial use)
            QuestPDF.Settings.License = LicenseType.Community;

            var document = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4.Landscape());
                    page.Margin(30);
                    page.DefaultTextStyle(x => x.FontSize(10));

                    // Content
                    page.Content().Element(content => ComposeContent(content, auditLogs, fromDate, toDate, reportCode));

                    // Footer
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
                // === Heading Row with Logo + True Centered Title + Report Code ===
                column.Item().PaddingBottom(20).Row(row =>
                {
                    // Left: Logo (60px wide)
                    var logoPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "lib", "Images", "cropped_logoblack.png");
                    if (File.Exists(logoPath))
                    {
                        var logoBytes = File.ReadAllBytes(logoPath);

                        row.ConstantItem(60)
                            .AlignMiddle()
                            .Element(c => c.Image(logoBytes));
                    }
                    else
                    {
                        row.ConstantItem(60); // empty if no logo
                    }

                    // Middle: Title (centered)
                    row.RelativeItem().AlignMiddle().AlignCenter().Text("Audit Log Report")
                        .FontSize(24).Bold().FontColor(Colors.Black);

                    // Right: Report Code (if provided)
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

                // Rest of your existing content implementation...
                // (keeping the rest of the method unchanged)

                // Separator line
                column.Item().PaddingBottom(20).Text(new string('=', 130))
                    .FontSize(10).FontFamily("Courier New");

                // Data section using column/row approach
                if (auditLogs.Any())
                {
                    column.Item().PaddingBottom(20).Column(dataColumn =>
                    {
                        // Header - only once
                        dataColumn.Item().Row(headerRow =>
                        {
                            headerRow.RelativeColumn(2).Element(InvisibleCellStyle).Text("Time Stamp").Bold().FontSize(12);
                            headerRow.RelativeColumn(3).Element(InvisibleCellStyle).Text("User").Bold().FontSize(12);
                            headerRow.RelativeColumn(2).Element(InvisibleCellStyle).Text("User Role").Bold().FontSize(12);
                            headerRow.RelativeColumn(1.5f).Element(InvisibleCellStyle).Text("Action").Bold().FontSize(12);
                            headerRow.RelativeColumn(4).Element(InvisibleCellStyle).Text("Description").Bold().FontSize(12);
                        });

                        // Data rows - each row is kept together with ShowEntire
                        foreach (var log in auditLogs.OrderByDescending(l => l.Timestamp))
                        {
                            dataColumn.Item().ShowEntire().Row(dataRow =>
                            {
                                dataRow.RelativeColumn(2).Element(InvisibleCellStyle).Text(log.Timestamp.ToString("dd/MM/yyyy HH:mm:ss"));
                                dataRow.RelativeColumn(3).Element(InvisibleCellStyle).Text(log.AdminFullName ?? "N/A");
                                dataRow.RelativeColumn(2).Element(InvisibleCellStyle).Text(GetUserRole(log));
                                dataRow.RelativeColumn(1.5f).Element(InvisibleCellStyle).Text(GetActionTypeDisplay(log.ActionType));
                                dataRow.RelativeColumn(4).Element(InvisibleCellStyle).Text(log.Description ?? "N/A");
                            });
                        }
                    });
                }

                // Bottom separator line
                column.Item().PaddingVertical(20).Text(new string('=', 130))
                    .FontSize(10).FontFamily("Courier New");

                // Date generated
                column.Item().PaddingBottom(10).Text($"Date generated: {DateTime.Now:dd MMMM yyyy HH:mm}")
                    .FontSize(12).Bold();

                // Time range information
                if (fromDate.HasValue && toDate.HasValue)
                {
                    var timeRangeText = GetTimeRangeDescription(fromDate.Value, toDate.Value);
                    column.Item().Text($"Time Range: {timeRangeText}")
                        .FontSize(12);
                }
                else if (fromDate.HasValue || toDate.HasValue)
                {
                    // Handle cases where only one date is provided
                    var dateText = fromDate?.ToString("dd MMMM yyyy") ?? toDate?.ToString("dd MMMM yyyy");
                    column.Item().Text($"Time Range: From {dateText}")
                        .FontSize(12);
                }

                // No records message
                if (!auditLogs.Any())
                {
                    column.Item().PaddingTop(20).AlignCenter().Text("No audit logs found for the selected period.")
                        .FontSize(12).Italic().FontColor(Colors.Grey.Darken1);
                }
            });
        }

        // Keep all your existing helper methods unchanged...
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