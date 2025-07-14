using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SABC_Phase2.Models.OVRS;
using SABC_Phase2.Models.Tender;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SABC_Phase2.Services
{
    /// <summary>
    /// Service responsible for generating a supplier report PDF for a given tender.
    /// Uses QuestPDF to build a styled, tabular report of all supplier applications.
    /// </summary>
    public class TenderReportPdfService
    {
        /// <summary>
        /// Generates a PDF report for a specific tender, listing all supplier applications.
        /// </summary>
        /// <param name="tender">The tender for which the report is generated.</param>
        /// <param name="applications">A list of supplier info viewmodels for the tender.</param>
        /// <returns>Byte array representing the generated PDF file.</returns>
        public byte[] GenerateSupplierReport(Tender tender, List<OVRS_UserPdfInfoViewModel> applications)
        {
            // Build the absolute path to the logo image in the wwwroot folder.
            var logoPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "lib", "Images", "cropped_logoblack.png");

            // Load the logo image as a byte array if it exists, otherwise logoBytes is null.
            byte[] logoBytes = File.Exists(logoPath) ? File.ReadAllBytes(logoPath) : null;

            // Calculate the number of local and foreign suppliers by checking the SupplierType property (case-insensitive).
            int localCount = applications.Count(a => a.SupplierType.ToLower() == "local");
            int foreignCount = applications.Count(a => a.SupplierType.ToLower() == "foreign");
            int grandTotal = localCount + foreignCount;

            // Use QuestPDF's fluent API to create the PDF document.
            return Document.Create(container =>
            {
                container.Page(page =>
                {
                    // Set page margins and A4 size.
                    page.Margin(30);
                    page.Size(PageSizes.A4);
                    page.DefaultTextStyle(x => x.FontSize(12));

                    // HEADER SECTION: logo, report title, and tender closing date/time.
                    page.Header().Row(row =>
                    {
                        // Left column: SABC logo (if available), scaled to fit width.
                        row.ConstantColumn(80).AlignMiddle().AlignLeft().Element(e =>
                        {
                            if (logoBytes != null)
                                e.Image(logoBytes).FitWidth();
                        });

                        // Center column: Report title and tender closing date/time.
                        row.RelativeColumn().Column(col =>
                        {
                            col.Item().Text("Supplier Report")
                                .FontSize(20)
                                .Bold()
                                .AlignCenter();

                            col.Item().Text(
                                $"Closed date & Time: {tender.ClosingDate:yyyy/MM/dd} @ " +
                                (tender.ClosingTime.HasValue ? tender.ClosingTime.Value.ToString(@"hh\:mm") : "--:--"))
                                .FontSize(11)
                                .AlignCenter();
                        });
                    });

                    // MAIN CONTENT: supplier table and statistics.
                    page.Content().Column(col =>
                    {
                        // Add vertical spacing between elements.
                        col.Spacing(10);

                        // Decorative separator line.
                        col.Item().PaddingVertical(5)
                            .Text("============================================================================================")
                            .FontSize(10);

                        // Table header row with column titles.
                        col.Item().Row(row =>
                        {
                            row.RelativeColumn().Text("Tender Number").Bold();
                            row.RelativeColumn().Text("Company Name").Bold();
                            row.RelativeColumn().Text("Type of Supplier").Bold();
                            row.RelativeColumn().Text("Date & Time").Bold();
                        });

                        // Table data rows: one per supplier application.
                        foreach (var app in applications)
                        {
                            col.Item().Row(row =>
                            {
                                // Tender number (from the tender itself).
                                row.RelativeColumn().Text(tender.TenderNumber ?? "-").WrapAnywhere();

                                // Company name and supplier type from the viewmodel.
                                row.RelativeColumn().Text(app.CompanyName ?? "-").WrapAnywhere();
                                row.RelativeColumn().Text(app.SupplierType ?? "-").WrapAnywhere();

                                // Closing date and time from the tender.
                                row.RelativeColumn().Text(
                                    $"{tender.ClosingDate:dd/MM/yyyy} @ " +
                                    (tender.ClosingTime.HasValue ? tender.ClosingTime.Value.ToString(@"hh\:mm\:ss") : "--:--:--")
                                ).WrapAnywhere();
                            });
                        }

                        // Decorative separator line.
                        col.Item().PaddingVertical(5)
                            .Text("============================================================================================")
                            .FontSize(10);

                        // Totals section: show count of local and foreign suppliers.
                        col.Item().Row(row =>
                        {
                            row.RelativeColumn(2).Text("Total Applicant").Bold();
                            row.RelativeColumn().Text("Local Supplier").AlignRight();
                            row.RelativeColumn().Text(localCount.ToString()).AlignRight();
                        });
                        col.Item().Row(row =>
                        {
                            row.RelativeColumn(2).Text("");
                            row.RelativeColumn().Text("Foreign Supplier").AlignRight();
                            row.RelativeColumn().Text(foreignCount.ToString()).AlignRight();
                        });

                        // Decorative separator line.
                        col.Item().PaddingVertical(5)
                            .Text("============================================================================================")
                            .FontSize(10);

                        // Grand total row: show total number of suppliers for the tender.
                        col.Item().Row(row =>
                        {
                            row.RelativeColumn(3).Text("Grand Total").Bold();
                            row.RelativeColumn().Text(grandTotal.ToString()).Bold().AlignRight();
                        });
                    });
                });
            }).GeneratePdf(); // Generate the PDF and return as a byte array.
        }
    }
}