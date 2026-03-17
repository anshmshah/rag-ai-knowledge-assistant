using LocalRagAPI.Models;
using Microsoft.Extensions.Hosting;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace LocalRagAPI.Services
{
    public class DocumentIngestionWorker : BackgroundService
    {
        private readonly DocumentIngestionQueue _queue;
        private readonly IngestionJobStore _jobStore;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<DocumentIngestionWorker> _logger;

        public DocumentIngestionWorker(DocumentIngestionQueue queue, IngestionJobStore jobStore, IServiceProvider serviceProvider, ILogger<DocumentIngestionWorker> logger)
        {
            _queue = queue;
            _jobStore = jobStore;
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("DocumentIngestionWorker started");

            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                var jobId = request.JobId;
                try
                {
                    _logger.LogInformation("Starting ingestion job {JobId} for document {Doc}", jobId, request.DocumentName);
                    _jobStore.MarkStarted(jobId);

                    using var scope = _serviceProvider.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<DocumentProcessor>();

                    var progress = new Progress<(int completed, int total)>(p =>
                    {
                        _jobStore.UpdateProgress(jobId, p.completed, p.total);
                    });

                    await processor.ProcessAsync(request, progress);

                    _jobStore.MarkCompleted(jobId);
                    _logger.LogInformation("Completed ingestion job {JobId}", jobId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Job {JobId} failed", jobId);
                    _jobStore.MarkFailed(jobId, ex.Message);
                }
            }
        }
    }
}
