using System;

namespace LocalRagAPI.Models
{
    public enum IngestionJobState
    {
        Queued,
        Processing,
        Completed,
        Failed
    }

    public class IngestionJobStatus
    {
        public string JobId { get; set; }
        public IngestionJobState State { get; set; }
        public int CompletedBatches { get; set; }
        public int TotalBatches { get; set; }
        public string Error { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
    }
}
