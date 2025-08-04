using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;

namespace SABC_Phase2.Models.OVRS
{
    /// <summary>
    /// ViewModel used to transfer data between the tender application draft UI and the backend controller.
    /// Encapsulates all data required for displaying and submitting a draft tender application,
    /// including persisted documents and any files uploaded during the current user session.
    /// </summary>
    public class TenderApplicationDraftViewModel
    {
        /// <summary>
        /// Database primary key for the draft (nullable for new drafts).
        /// </summary>
        public int? Id { get; set; }

        /// <summary>
        /// Globally unique identifier for the draft (used for lookups and updates).
        /// </summary>
        public Guid? DraftId { get; set; }

        /// <summary>
        /// The user's unique identifier (links the draft to a specific user).
        /// </summary>
        public int? OVRS_UserId { get; set; }

        /// <summary>
        /// The associated tender's unique identifier (nullable for flexibility).
        /// </summary>
        public int? TenderId { get; set; }

        /// <summary>
        /// List of documents already saved as part of this draft (persisted in the database).
        /// Used to display existing draft documents for editing or review.
        /// </summary>
        public List<TenderApplicationDraftDocument> ExistingDocuments { get; set; }

        /// <summary>
        /// List of files uploaded during the current form submission.
        /// These files are not yet persisted and need to be processed server-side.
        /// </summary>
        public List<IFormFile> UploadedFiles { get; set; }

        public List<int> DocumentsToDelete { get; set; } = new();
    }
}