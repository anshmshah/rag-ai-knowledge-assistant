namespace LocalRagAPI.Models
{
    public class DocumentIngestionRequest
    {
        public string JobId { get; set; }
        public string DocumentName { get; set; }
        public string Text { get; set; }
        public string FileName { get; set; }
        // New for multi-user: optional owning user and document id
        public Guid? DocumentId { get; set; }
        public Guid? UserId { get; set; }
    }
}
