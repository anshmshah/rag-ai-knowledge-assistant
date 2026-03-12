using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace LocalRagAPI.Services
{
    public class QdrantService
    {
        private readonly QdrantClient _client;
        private const string COLLECTION = "documents";
        private readonly Microsoft.Extensions.Logging.ILogger<QdrantService> _logger;

        public QdrantService(Microsoft.Extensions.Logging.ILogger<QdrantService> logger)
        {
            _logger = logger;
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
            _client = new QdrantClient("localhost", 6334);
        }

        // =========================
        // CREATE COLLECTION
        // =========================

        public async Task InitializeCollection()
        {
            var collections = await _client.ListCollectionsAsync();

            if (collections.Contains(COLLECTION))
            {
                await _client.DeleteCollectionAsync(COLLECTION);
            }

            await _client.CreateCollectionAsync(
                COLLECTION,
                new VectorParams
                {
                    Size = 768,
                    Distance = Distance.Cosine
                });
        }

        // =========================
        // INSERT CHUNK
        // =========================

        public async Task InsertChunk(string document, string content, float[] embedding)
        {
            var point = new PointStruct
            {
                Id = new PointId { Uuid = Guid.NewGuid().ToString() },
                Vectors = embedding
            };

            point.Payload.Add("document", document);
            point.Payload.Add("content", content);

            await _client.UpsertAsync(
                COLLECTION,
                new List<PointStruct> { point }
            );
        }

        // =========================
        // VECTOR SEARCH
        // =========================

        public async Task<List<LocalRagAPI.Models.SearchResultItem>> Search(float[] embedding, string documentFilter = null)
        {
            return await Search(embedding, documentFilter, 20);
        }

        public async Task<List<LocalRagAPI.Models.SearchResultItem>> Search(float[] embedding, string documentFilter = null, int limit = 20)
        {
            Filter filter = null;

            // If document filter is provided, search only that document
            if (!string.IsNullOrEmpty(documentFilter))
            {
                filter = new Filter
                {
                    Must =
                    {
                        new Condition
                        {
                            Field = new FieldCondition
                            {
                                Key = "document",
                                Match = new Match
                                {
                                    Keyword = documentFilter
                                }
                            }
                        }
                    }
                };
            }

            var results = await _client.SearchAsync(
                collectionName: COLLECTION,
                vector: embedding,
                limit: (uint)limit,
                filter: filter
            );

            return results
                .Select(r => new LocalRagAPI.Models.SearchResultItem
                {
                    Content = r.Payload.ContainsKey("content") ? r.Payload["content"].StringValue : string.Empty,
                    Document = r.Payload.ContainsKey("document") ? r.Payload["document"].StringValue : string.Empty,
                    Score = (float)r.Score,
                    PointId = r.Id != null ? (r.Id.Uuid ?? r.Id.ToString() ?? string.Empty) : string.Empty
                })
                .ToList();
        }

        public async Task<List<LocalRagAPI.Models.SearchResultItem>> KeywordSearch(string query, string documentFilter = null, int limit = 20)
        {
            var mustConditions = new List<Condition>();

            // keyword match
            mustConditions.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = "content",
                    Match = new Match
                    {
                        Text = query
                    }
                }
            });

            // optional document filter
            if (!string.IsNullOrEmpty(documentFilter))
            {
                mustConditions.Add(new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "document",
                        Match = new Match
                        {
                            Keyword = documentFilter
                        }
                    }
                });
            }

            var filter = new Filter { Must = { mustConditions } };

            var scroll = await _client.ScrollAsync(
                collectionName: COLLECTION,
                filter: filter,
                limit: (uint)limit
            );

            return scroll.Result
                .Select(r => new LocalRagAPI.Models.SearchResultItem
                {
                    Content = r.Payload.ContainsKey("content") ? r.Payload["content"].StringValue : string.Empty,
                    Document = r.Payload.ContainsKey("document") ? r.Payload["document"].StringValue : string.Empty,
                    Score = 0f,
                    PointId = r.Id != null ? (r.Id.Uuid ?? r.Id.ToString() ?? string.Empty) : string.Empty
                })
                .ToList();
        }

        public async Task BatchUpsertAsync(List<PointStruct> points)
        {
            if (points == null || !points.Any())
                return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await _client.UpsertAsync(
                    COLLECTION,
                    points
                );
            }
            finally
            {
                sw.Stop();
                _logger?.LogInformation("Upserted {Count} points in {Elapsed}ms", points.Count, sw.ElapsedMilliseconds);
            }
        }

        public async Task<bool> HasPointsAsync(string documentFilter = null)
        {
            Filter filter = null;

            if (!string.IsNullOrEmpty(documentFilter))
            {
                filter = new Filter
                {
                    Must =
                    {
                        new Condition
                        {
                            Field = new FieldCondition
                            {
                                Key = "document",
                                Match = new Match
                                {
                                    Keyword = documentFilter
                                }
                            }
                        }
                    }
                };
            }

            var scroll = await _client.ScrollAsync(
                collectionName: COLLECTION,
                filter: filter,
                limit: (uint)1
            );

            return scroll.Result != null && scroll.Result.Any();
        }

        public async Task DeleteByDocumentAsync(string documentName)
        {
            var filter = new Filter
            {
                Must =
        {
            new Condition
            {
                Field = new FieldCondition
                {
                    Key = "document",
                    Match = new Match
                    {
                        Keyword = documentName
                    }
                }
            }
        }
            };

            await _client.DeleteAsync(
                collectionName: COLLECTION,
                filter: filter
            );
        }
    }
}