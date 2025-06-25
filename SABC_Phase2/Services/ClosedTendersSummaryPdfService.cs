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
    public static class ClosedTendersSummaryPdfService
    {
        public static byte[] GenerateSummaryReport(List<ClosedTenderSummaryRow> rows, DateTime startDate, DateTime endDate, string reportCode = null)
        {
            // Group by TenderType for summary rows
            var grouped = rows
                .GroupBy(r => r.TenderType)
                .Select(g => new
                {
                    TenderType = g.Key,
                    ClosedTenderCount = g.Sum(x => x.ClosedTenderCount),
                    LocalSuppliers = g.Sum(x => x.LocalSuppliers),
                    ForeignSuppliers = g.Sum(x => x.ForeignSuppliers),
                    TotalApplicants = g.Sum(x => x.TotalApplicants),
                    AvgApplicants = g.Sum(x => x.TotalApplicants) / (g.Sum(x => x.ClosedTenderCount) == 0 ? 1m : g.Sum(x => x.ClosedTenderCount))
                })
                .ToList();

            int totalClosed = grouped.Sum(g => g.ClosedTenderCount);
            int totalLocal = grouped.Sum(g => g.LocalSuppliers);
            int totalForeign = grouped.Sum(g => g.ForeignSuppliers);
            int totalApplicants = grouped.Sum(g => g.TotalApplicants);
            decimal avgApplicants = totalClosed == 0 ? 0 : (decimal)totalApplicants / totalClosed;

            // Load logo image as byte array
            byte[] logoBytes = null;
            var logoPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "lib", "Images", "logoblack.png");
            if (File.Exists(logoPath))
                logoBytes = File.ReadAllBytes(logoPath);

            // Table cell style for header
            Func<IContainer, IContainer> CellStyleHeader = container =>
                container
                    .Background("#FFA800")
                    .PaddingVertical(10)
                    .PaddingHorizontal(5)
                    .AlignCenter()
                    .AlignMiddle();
            // .BorderRadius(topLeft: 8, topRight: 8) // Remove if not supported

            // Table cell style for body rows
            Func<bool, Func<IContainer, IContainer>> CellStyle = even => cell =>
                cell
                    .Background(even ? "#F4F4F4" : "#EDEDED")
                    .PaddingVertical(9)
                    .PaddingHorizontal(5)
                    .AlignCenter()
                    .AlignMiddle();

            return Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Margin(30);
                    page.Size(PageSizes.A4);
                    page.DefaultTextStyle(x => x.FontSize(12).FontFamily("Segoe UI"));

                    page.Header().Element(header =>
     header
         .PaddingBottom(20)
         .Row(row =>
         {
             // Logo left
             row.ConstantColumn(90).AlignMiddle().AlignLeft().Element(e =>
             {
                 if (logoBytes != null)
                     e.Height(70).Width(70).Image(logoBytes);
             });

             // Center
             row.RelativeColumn().Column(col =>
             {
                 col.Item().Text("Tender Report")
                     .FontSize(22)
                     .Bold()
                     .AlignCenter();

                 col.Item().Text($"{startDate:yyyy/MM/dd} - {endDate:yyyy/MM/dd}")
                     .FontSize(12)
                     .AlignCenter();
             });

             // Report code right (optional)
             row.ConstantColumn(120).AlignMiddle().AlignRight().Element(e =>
             {
                 if (!string.IsNullOrWhiteSpace(reportCode))
                 {
                     e.Text(reportCode)
                         .FontColor(Colors.Red.Medium)
                         .FontSize(16)
                         .Bold();
                 }
             });
         })
 );

                    page.Content().Column(col =>
                    {
                        col.Spacing(10);

                        // Table
                        col.Item().Element(tableContainer =>
                        {
                            tableContainer.Table(table =>
                            {
                                table.ColumnsDefinition(columns =>
                                {
                                    columns.RelativeColumn();
                                    columns.RelativeColumn();
                                    columns.RelativeColumn();
                                    columns.RelativeColumn();
                                    columns.RelativeColumn();
                                    columns.RelativeColumn();
                                });

                                // Header row
                                table.Header(headerRow =>
                                {
                                    headerRow.Cell().Element(CellStyleHeader).Text("Tender Type").FontSize(13).Bold().FontColor("#fff");
                                    headerRow.Cell().Element(CellStyleHeader).Text("No. of Closed Tenders").FontSize(13).Bold().FontColor("#fff");
                                    headerRow.Cell().Element(CellStyleHeader).Text("Local Supplier").FontSize(13).Bold().FontColor("#fff");
                                    headerRow.Cell().Element(CellStyleHeader).Text("Foreign Supplier").FontSize(13).Bold().FontColor("#fff");
                                    headerRow.Cell().Element(CellStyleHeader).Text("Total Applicants").FontSize(13).Bold().FontColor("#fff");
                                    headerRow.Cell().Element(CellStyleHeader).Text("Average Applicants per Tender").FontSize(13).Bold().FontColor("#fff");
                                });

                                // Data rows
                                int rowIdx = 0;
                                foreach (var g in grouped)
                                {
                                    bool even = rowIdx++ % 2 == 0;
                                    table.Cell().Element(CellStyle(even)).Text(g.TenderType ?? "-");
                                    table.Cell().Element(CellStyle(even)).Text(g.ClosedTenderCount.ToString("00"));
                                    table.Cell().Element(CellStyle(even)).Text(g.LocalSuppliers.ToString());
                                    table.Cell().Element(CellStyle(even)).Text(g.ForeignSuppliers.ToString());
                                    table.Cell().Element(CellStyle(even)).Text(g.TotalApplicants.ToString());
                                    table.Cell().Element(CellStyle(even)).Text($"{g.AvgApplicants:0.0}%");
                                }
                            });
                        });

                        // Totals row with dashed lines
                        col.Item().PaddingTop(20).Element(totalContainer =>
                        {
                            totalContainer.Column(c2 =>
                            {
                                // Top dashed line
                                c2.Item().Row(row =>
                                {
                                    row.RelativeItem().Text("-----------------------------------------------------------" +
                                        "--------------------------------------------------------------------------")
                                    .FontSize(10)
                                    .FontColor("#555");
                                });

                                // Totals row
                                c2.Item().Row(row =>
                                {
                                    row.RelativeColumn().Text("Total").Bold().FontSize(13);
                                    row.RelativeColumn().Text(totalClosed.ToString()).Bold().FontSize(13).AlignCenter();
                                    row.RelativeColumn().Text(totalLocal.ToString()).Bold().FontSize(13).AlignCenter();
                                    row.RelativeColumn().Text(totalForeign.ToString()).Bold().FontSize(13).AlignCenter();
                                    row.RelativeColumn().Text(totalApplicants.ToString()).Bold().FontSize(13).AlignCenter();
                                    row.RelativeColumn().Text($"{avgApplicants:0.0}%").Bold().FontSize(13).AlignCenter();
                                });

                                // Bottom dashed line
                                c2.Item().Row(row =>
                                {
                                    row.RelativeItem().Text("-----------------------------------------------------------" +
                                        "--------------------------------------------------------------------------")
                                    .FontSize(10)
                                    .FontColor("#555");
                                });
                            });
                        });
                    });

                    // Footer: Page number
                    page.Footer().AlignRight().Text(x =>
                    {
                        x.Span("Page ").FontSize(11).FontColor("#666");
                        x.CurrentPageNumber().FontSize(11).FontColor("#666");
                        x.Span(" of ").FontSize(11).FontColor("#666");
                        x.TotalPages().FontSize(11).FontColor("#666");
                    });
                });
            }).GeneratePdf();
        }
    }
}