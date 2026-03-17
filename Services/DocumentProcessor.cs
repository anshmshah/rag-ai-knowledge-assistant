using LocalRagAPI.Models;
using System.Text;

namespace LocalRagAPI.Services
{
    public class DocumentProcessor
    {
        private readonly JinaEmbeddingService _embeddingService;
        private readonly QdrantService _qdrant;
        private readonly Microsoft.Extensions.Logging.ILogger<DocumentProcessor> _logger;

        public DocumentProcessor(JinaEmbeddingService embeddingService, QdrantService qdrant, Microsoft.Extensions.Logging.ILogger<DocumentProcessor> logger)
        {
            _embeddingService = embeddingService;
            _qdrant = qdrant;
            _logger = logger;
        }

        public async Task ProcessAsync(DocumentIngestionRequest request, IProgress<(int completed, int total)> progress = null)
        {
            var text = request.Text ?? string.Empty;
            var documentName = request.DocumentName ?? request.FileName ?? "uploaded-document";

            // Split into sentences
            var sentences = text
                .Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            int chunkSentenceSize = 6;
            int overlap = 2;
            int maxChunks = 300;

            var chunks = new List<string>();

            for (int i = 0; i < sentences.Count; i += (chunkSentenceSize - overlap))
            {
                var chunkSentences = sentences.Skip(i).Take(chunkSentenceSize).ToList();
                if (!chunkSentences.Any()) break;
                var chunkText = string.Join(". ", chunkSentences) + ".";
                chunks.Add(chunkText);
                if (chunks.Count >= maxChunks) break;
            }

            int batchSize = 256;
            var batches = new List<List<string>>();
            for (int i = 0; i < chunks.Count; i += batchSize)
            {
                batches.Add(chunks.Skip(i).Take(batchSize).ToList());
            }

            int completed = 0;
            for (int b = 0; b < batches.Count; b++)
            {
                var batch = batches[b];
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var embBatch = await _embeddingService.GenerateEmbeddings(batch);
                sw.Stop();
                _logger?.LogInformation("Processor: Embedding batch {BatchIndex} size={Count} took {Elapsed}ms", b, embBatch.Count, sw.ElapsedMilliseconds);

                var points = new List<Qdrant.Client.Grpc.PointStruct>();
                for (int j = 0; j < embBatch.Count; j++)
                {
                    var point = new Qdrant.Client.Grpc.PointStruct
                    {
                        Id = new Qdrant.Client.Grpc.PointId { Uuid = Guid.NewGuid().ToString() },
                        Vectors = embBatch[j]
                    };

                    // core payload
                    point.Payload.Add("document", documentName);
                    point.Payload.Add("content", batch[j]);

                    // attach ownership metadata when available
                    if (request.UserId.HasValue)
                    {
                        point.Payload.Add("user_id", request.UserId.Value.ToString());
                    }

                    if (request.DocumentId.HasValue)
                    {
                        point.Payload.Add("document_id", request.DocumentId.Value.ToString());
                    }

                    points.Add(point);
                }

                var swUpsert = System.Diagnostics.Stopwatch.StartNew();
                await _qdrant.BatchUpsertAsync(points);
                swUpsert.Stop();
                _logger?.LogInformation("Processor: Upsert batch {BatchIndex} points={Count} took {Elapsed}ms", b, points.Count, swUpsert.ElapsedMilliseconds);

                completed++;
                progress?.Report((completed, batches.Count));
            }
        }
    }
}
