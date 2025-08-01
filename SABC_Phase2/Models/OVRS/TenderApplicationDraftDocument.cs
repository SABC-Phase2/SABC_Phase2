using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SABC_Phase2.Models.OVRS
{
    /// <summary>
    /// Represents a document/file attached to a tender application draft.
    /// This entity is used to store metadata for each document uploaded by a user as part of their draft tender application.
    /// Documents are linked to their parent draft via a foreign key relationship.
    /// </summary>
    public class TenderApplicationDraftDocument
    {
        /// <summary>
        /// Primary key for the document record (auto-increment).
        /// </summary>
        [Key]
        public int Id { get; set; }

        /// <summary>
        /// Original file name as uploaded by the user.
        /// </summary>
        [Required]
        public string FileName { get; set; }

        /// <summary>
        /// URL to the file in SharePoint.
        /// </summary>
        [Required]
        public string SharePointPath { get; set; }

        /// <summary>
        /// Foreign key to associate this document with a specific tender application draft.
        /// </summary>
        public int TenderApplicationDraftId { get; set; }

        /// <summary>
        /// Navigation property for Entity Framework Core.
        /// Provides access to the parent tender application draft entity.
        /// </summary>
        [ForeignKey("TenderApplicationDraftId")]
        public TenderApplicationDraft TenderApplicationDraft { get; set; }
    }
}