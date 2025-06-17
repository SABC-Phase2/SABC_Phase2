using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace SABC_Phase2.Models.Tender
{
    public class TenderEditDto
    {
        public Guid? DraftId { get; set; }
        public int Id { get; set; }
        public List<int> DocumentsToDelete { get; set; } = new();
        public DateTime? ScheduledPublishDateTime { get; set; }

        [Required]
        public string TenderType { get; set; }

        [Required]
        public string TenderNumber { get; set; }

        [Required]
        [DataType(DataType.Date)]
        public DateTime? ClosingDate { get; set; }

        [DataType(DataType.Time)]
        public TimeSpan? ClosingTime { get; set; }

        [Required]
        public string Status { get; set; }

        [Required]
        public string Title { get; set; }

        public string Description { get; set; }

        // For new PDF uploads
        public List<IFormFile> UploadedFiles { get; set; } = new();

        // For displaying existing PDFs
        public List<TenderDocumentViewModel> ExistingDocuments { get; set; } = new();

        // --- Add these properties for scheduling ---
        public bool IsScheduled { get; set; }
        public DateTime? ScheduledDate { get; set; }
        public TimeSpan? ScheduledTime { get; set; }
    }
}