using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace SABC_Phase2.Models.Tender
{
    public class TenderViewModel
    {

       public Guid? DraftId { get; set; }
        public int Id { get; set; }
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

        [Required(ErrorMessage = "Please upload at least one PDF file.")]
        public List<IFormFile> UploadedFiles { get; set; }


        public bool IsScheduled { get; set; }
        public DateTime? ScheduledDate { get; set; }
        public TimeSpan? ScheduledTime { get; set; }

        //public List<TenderDocumentViewModel> ExistingDocuments { get; set; }
        public List<TenderDocumentViewModel> ExistingDocuments { get; set; } = new List<TenderDocumentViewModel>();

    }
}

