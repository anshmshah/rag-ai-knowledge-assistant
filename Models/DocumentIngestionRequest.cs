namespace LocalRagAPI.Models
{
    public class DocumentIngestionRequest
    {
        public string JobId { get; set; }
        public string DocumentName { get; set; }
        public string Text { get; set; }
        public string FileName { get; set; }
    }
}
