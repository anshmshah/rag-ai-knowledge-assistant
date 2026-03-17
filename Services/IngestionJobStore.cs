using System.Collections.Concurrent;
using LocalRagAPI.Models;

namespace LocalRagAPI.Services
{
    public class IngestionJobStore
    {
        private readonly ConcurrentDictionary<string, IngestionJobStatus> _store = new();

        public void AddJob(IngestionJobStatus job)
        {
            _store[job.JobId] = job;
        }

        public bool TryGet(string jobId, out IngestionJobStatus status)
        {
            return _store.TryGetValue(jobId, out status);
        }

        public void MarkStarted(string jobId)
        {
            if (_store.TryGetValue(jobId, out var s))
            {
                s.State = IngestionJobState.Processing;
                s.StartedAt = System.DateTime.UtcNow;
            }
        }

        public void UpdateProgress(string jobId, int completedBatches, int totalBatches)
        {
            if (_store.TryGetValue(jobId, out var s))
            {
                s.CompletedBatches = completedBatches;
                s.TotalBatches = totalBatches;
            }
        }

        public void MarkCompleted(string jobId)
        {
            if (_store.TryGetValue(jobId, out var s))
            {
                s.State = IngestionJobState.Completed;
                s.FinishedAt = System.DateTime.UtcNow;
                s.CompletedBatches = s.TotalBatches;
            }
        }

        public void MarkFailed(string jobId, string error)
        {
            if (_store.TryGetValue(jobId, out var s))
            {
                s.State = IngestionJobState.Failed;
                s.Error = error;
                s.FinishedAt = System.DateTime.UtcNow;
            }
        }
    }
}
