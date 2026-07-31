using System;

namespace LocalRagAPI.Models
{
    public class Document
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserId { get; set; }
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
        public DateTime? DeletedAt { get; set; }
        public string Sha256Hash { get; set; }
    }
}
