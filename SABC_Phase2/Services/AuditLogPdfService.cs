using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SABC_Phase2.Models.Tender;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SABC_Phase2.Services
{
    public class AuditLogPdfService
    {
        public byte[] GenerateAuditLogReport(List<AuditLog> auditLogs, DateTime? fromDate, DateTime? toDate)
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

                    // Header
                    page.Header().Element(ComposeHeader);

                    // Content
                    page.Content().Element(content => ComposeContent(content, auditLogs, fromDate, toDate));

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

        private void ComposeHeader(IContainer container)
        {
            container.Column(column =>
            {
                column.Item().BorderBottom(2).BorderColor(Colors.Green.Darken2).PaddingBottom(10).Row(row =>
                {
                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text("SABC Audit Log Report").FontSize(20).Bold().FontColor(Colors.Green.Darken2);
                        col.Item().Text($"Generated on: {DateTime.Now:dd MMMM yyyy HH:mm}").FontSize(10).FontColor(Colors.Grey.Darken2);
                    });
                });

                column.Item().PaddingTop(5);
            });
        }

        private void ComposeContent(IContainer container, List<AuditLog> auditLogs, DateTime? fromDate, DateTime? toDate)
        {
            container.Column(column =>
            {
                // Date Range Info
                column.Item().PaddingBottom(10).Row(row =>
                {
                    row.RelativeItem().Text(text =>
                    {
                        text.Span("Report Period: ").Bold();
                        if (fromDate.HasValue && toDate.HasValue)
                        {
                            text.Span($"{fromDate.Value:dd MMM yyyy} to {toDate.Value:dd MMM yyyy}");
                        }
                        else
                        {
                            text.Span("All Records");
                        }
                    });

                    row.RelativeItem().AlignRight().Text(text =>
                    {
                        text.Span("Total Records: ").Bold();
                        text.Span(auditLogs.Count.ToString());
                    });
                });

                // Summary Statistics
                column.Item().PaddingBottom(15).Background(Colors.Grey.Lighten3).Padding(10).Column(summaryCol =>
                {
                    summaryCol.Item().Text("Summary Statistics").FontSize(12).Bold().FontColor(Colors.Green.Darken2);
                    summaryCol.Item().PaddingTop(5).Row(summaryRow =>
                    {
                        var actionGroups = auditLogs.GroupBy(a => a.ActionType).OrderByDescending(g => g.Count());

                        summaryRow.RelativeItem().Column(col =>
                        {
                            col.Item().Text("Actions Breakdown:").Bold().FontSize(9);
                            foreach (var group in actionGroups)
                            {
                                col.Item().Text($"• {group.Key}: {group.Count()}").FontSize(8);
                            }
                        });

                        summaryRow.RelativeItem().Column(col =>
                        {
                            var uniqueAdmins = auditLogs.Select(a => a.AdminEmail).Distinct().Count();
                            col.Item().Text($"Unique Administrators: {uniqueAdmins}").Bold().FontSize(9);

                            var topAdmin = auditLogs.GroupBy(a => a.AdminFullName)
                                .OrderByDescending(g => g.Count())
                                .FirstOrDefault();
                            if (topAdmin != null)
                            {
                                col.Item().Text($"Most Active: {topAdmin.Key} ({topAdmin.Count()} actions)").FontSize(8);
                            }
                        });
                    });
                });

                // Audit Logs Table
                column.Item().Table(table =>
                {
                    // Define columns with appropriate widths
                    table.ColumnsDefinition(columns =>
                    {
                        columns.ConstantColumn(40);  // ID
                        columns.RelativeColumn(2);    // Admin Name
                        columns.RelativeColumn(2);    // Email
                        columns.RelativeColumn(1.5f); // Action Type
                        columns.RelativeColumn(4);    // Description
                        columns.RelativeColumn(1.5f); // Timestamp
                    });

                    // Header
                    table.Header(header =>
                    {
                        header.Cell().Element(CellStyle).Background(Colors.Green.Darken2).Text("ID").FontColor(Colors.White).Bold();
                        header.Cell().Element(CellStyle).Background(Colors.Green.Darken2).Text("Admin Name").FontColor(Colors.White).Bold();
                        header.Cell().Element(CellStyle).Background(Colors.Green.Darken2).Text("Email").FontColor(Colors.White).Bold();
                        header.Cell().Element(CellStyle).Background(Colors.Green.Darken2).Text("Action Type").FontColor(Colors.White).Bold();
                        header.Cell().Element(CellStyle).Background(Colors.Green.Darken2).Text("Description").FontColor(Colors.White).Bold();
                        header.Cell().Element(CellStyle).Background(Colors.Green.Darken2).Text("Timestamp").FontColor(Colors.White).Bold();
                    });

                    // Rows
                    foreach (var log in auditLogs.OrderByDescending(l => l.Timestamp))
                    {
                        var rowColor = auditLogs.IndexOf(log) % 2 == 0 ? Colors.White : Colors.Grey.Lighten4;

                        table.Cell().Element(CellStyle).Background(rowColor).Text(log.Id.ToString());
                        table.Cell().Element(CellStyle).Background(rowColor).Text(log.AdminFullName ?? "N/A");
                        table.Cell().Element(CellStyle).Background(rowColor).Text(log.AdminEmail ?? "N/A").FontSize(8);
                        table.Cell().Element(CellStyle).Background(rowColor).Text(GetActionTypeDisplay(log.ActionType));
                        table.Cell().Element(CellStyle).Background(rowColor).Text(log.Description ?? "N/A").FontSize(8);
                        table.Cell().Element(CellStyle).Background(rowColor).Text(log.Timestamp.ToString("dd/MM/yyyy HH:mm")).FontSize(8);
                    }
                });

                // No records message
                if (!auditLogs.Any())
                {
                    column.Item().PaddingTop(20).AlignCenter().Text("No audit logs found for the selected period.")
                        .FontSize(12).Italic().FontColor(Colors.Grey.Darken1);
                }
            });
        }

        private IContainer CellStyle(IContainer container)
        {
            return container.Border(1).BorderColor(Colors.Grey.Lighten2).Padding(5);
        }

        private string GetActionTypeDisplay(string actionType)
        {
            // Format action type for better readability
            if (string.IsNullOrEmpty(actionType)) return "N/A";

            return actionType switch
            {
                "CreateDraft" => "Create Draft",
                "EditDraft" => "Edit Draft",
                "DeleteDraft" => "Delete Draft",
                "PublishTender" => "Publish Tender",
                "CloseTender" => "Close Tender",
                "AwardTender" => "Award Tender",
                _ => actionType
            };
        }
    }
}