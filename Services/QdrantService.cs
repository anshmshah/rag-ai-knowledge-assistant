using System.Text;
using System.Text.Json;

namespace LocalRagAPI.Services
{
    public class QdrantService
    {
        private readonly HttpClient _httpClient;
        private readonly string _url;
        private readonly string _apiKey;
        private readonly ILogger<QdrantService> _logger;

        private const string COLLECTION = "documents";

        public QdrantService(ILogger<QdrantService> logger, IConfiguration config)
        {
            _logger = logger;

            _url = config["Qdrant:Url"];
            _apiKey = config["Qdrant:ApiKey"];

            if (string.IsNullOrEmpty(_url))
                throw new Exception("Qdrant URL is not configured");

            if (string.IsNullOrEmpty(_apiKey))
                throw new Exception("Qdrant API key is not configured");

            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("api-key", _apiKey);
        }

        // =========================
        // CREATE COLLECTION
        // =========================
        public async Task InitializeCollection()
        {
            var res = await _httpClient.GetAsync($"{_url}/collections/{COLLECTION}");

            if (res.IsSuccessStatusCode)
            {
                _logger.LogInformation("Collection exists");
                return;
            }

            var body = new
            {
                vectors = new
                {
                    size = 768,
                    distance = "Cosine"
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json"
            );

            var create = await _httpClient.PutAsync($"{_url}/collections/{COLLECTION}", content);

            if (!create.IsSuccessStatusCode)
            {
                var err = await create.Content.ReadAsStringAsync();
                throw new Exception($"Collection creation failed: {err}");
            }

            _logger.LogInformation("Collection created");
        }

        // =========================
        // UPSERT POINTS (MAIN)
        // =========================
        public async Task BatchUpsertAsync(List<(string content, string document, float[] vector, string userId, string documentId)> items)
        {
            var points = items.Select(i => new
            {
                id = Guid.NewGuid().ToString(),
                vector = i.vector,
                payload = new Dictionary<string, object>
                {
                    ["content"] = i.content,
                    ["document"] = i.document,
                    ["user_id"] = i.userId ?? "",
                    ["document_id"] = i.documentId ?? ""
                }
            });

            var body = new { points };

            var contentJson = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json"
            );

            var res = await _httpClient.PutAsync($"{_url}/collections/{COLLECTION}/points", contentJson);

            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync();
                throw new Exception($"Upsert failed: {err}");
            }

            _logger.LogInformation("Inserted {Count} points", items.Count);
        }

        // =========================
        // TEMP DISABLE (IMPORTANT)
        // =========================
        public Task<bool> HasPointsAsync(string doc, string userId)
        {
            return Task.FromResult(true); // TEMP FIX
        }

        public Task<List<LocalRagAPI.Models.SearchResultItem>> Search(float[] embedding, string doc, int limit, string userId)
        {
            return Task.FromResult(new List<LocalRagAPI.Models.SearchResultItem>());
        }

        public Task<List<LocalRagAPI.Models.SearchResultItem>> KeywordSearch(string query, string doc, int limit, string userId)
        {
            return Task.FromResult(new List<LocalRagAPI.Models.SearchResultItem>());
        }

        public Task DeleteByDocumentAsync(string name) => Task.CompletedTask;
        public Task DeleteByDocumentAndUserAsync(string name, string userId) => Task.CompletedTask;
    }
}

//using Qdrant.Client;
//using Qdrant.Client.Grpc;
//using Microsoft.Extensions.Configuration;

//namespace LocalRagAPI.Services
//{
//    public class QdrantService
//    {
//        private readonly QdrantClient _client;
//        private const string COLLECTION = "documents";
//        private readonly Microsoft.Extensions.Logging.ILogger<QdrantService> _logger;
//        private readonly bool _recreateOnStartup;

//        public QdrantService(ILogger<QdrantService> logger, IConfiguration config)
//        {
//            _logger = logger;

//            var host = config["Qdrant:Host"];
//            var port = int.Parse(config["Qdrant:Port"] ?? "6334");
//            var apiKey = config["Qdrant:ApiKey"];

//            if (string.IsNullOrEmpty(host))
//                throw new Exception("Qdrant host not configured");

//            if (string.IsNullOrEmpty(apiKey))
//                throw new Exception("Qdrant API key not configured");

//            _recreateOnStartup = config.GetValue<bool>("Qdrant:RecreateOnStartup", false);

//            // ✅ gRPC WITH API KEY
//            _client = new QdrantClient(host, port, apiKey: apiKey);
//        }

//        // =========================
//        // CREATE COLLECTION
//        // =========================

//        public async Task InitializeCollection()
//        {
//            var collections = await _client.ListCollectionsAsync();

//            if (collections.Contains(COLLECTION))
//            {
//                if (_recreateOnStartup)
//                {
//                    _logger?.LogWarning("Qdrant collection '{Collection}' exists and will be recreated because Qdrant:RecreateOnStartup=true", COLLECTION);
//                    await _client.DeleteCollectionAsync(COLLECTION);

//                    await _client.CreateCollectionAsync(
//                        COLLECTION,
//                        new VectorParams
//                        {
//                            Size = 768,
//                            Distance = Distance.Cosine
//                        });
//                }
//                else
//                {
//                    _logger?.LogInformation("Qdrant collection '{Collection}' already exists; skipping creation.", COLLECTION);
//                }
//            }
//            else
//            {
//                _logger?.LogInformation("Qdrant collection '{Collection}' does not exist and will be created.", COLLECTION);
//                await _client.CreateCollectionAsync(
//                    COLLECTION,
//                    new VectorParams
//                    {
//                        Size = 768,
//                        Distance = Distance.Cosine
//                    });
//            }
//        }

//        // =========================
//        // INSERT CHUNK
//        // =========================

//        public async Task InsertChunk(string document, string content, float[] embedding)
//        {
//            var point = new PointStruct
//            {
//                Id = new PointId { Uuid = Guid.NewGuid().ToString() },
//                Vectors = embedding
//            };

//            point.Payload.Add("document", document);
//            point.Payload.Add("content", content);

//            await _client.UpsertAsync(
//                COLLECTION,
//                new List<PointStruct> { point }
//            );
//        }

//        // =========================
//        // VECTOR SEARCH
//        // =========================

//        public async Task<List<LocalRagAPI.Models.SearchResultItem>> Search(float[] embedding, string documentFilter = null)
//        {
//            return await Search(embedding, documentFilter, 20);
//        }

//        public async Task<List<LocalRagAPI.Models.SearchResultItem>> Search(float[] embedding, string documentFilter = null, int limit = 20, string userId = null)
//        {
//            Filter filter = null;

//            // If document filter is provided, search only that document
//            if (!string.IsNullOrEmpty(documentFilter))
//            {
//                filter = new Filter
//                {
//                    Must =
//                    {
//                        new Condition
//                        {
//                            Field = new FieldCondition
//                            {
//                                Key = "document",
//                                Match = new Match
//                                {
//                                    Keyword = documentFilter
//                                }
//                            }
//                        }
//                    }
//                };
//            }

//            // If userId provided, add user filter
//            if (!string.IsNullOrEmpty(userId))
//            {
//                if (filter == null) filter = new Filter();

//                filter.Must.Add(new Condition
//                {
//                    Field = new FieldCondition
//                    {
//                        Key = "user_id",
//                        Match = new Match { Keyword = userId }
//                    }
//                });
//            }

//            var results = await _client.SearchAsync(
//                collectionName: COLLECTION,
//                vector: embedding,
//                limit: (uint)limit,
//                filter: filter
//            );

//            return results
//                .Select(r => new LocalRagAPI.Models.SearchResultItem
//                {
//                    Content = r.Payload.ContainsKey("content") ? r.Payload["content"].StringValue : string.Empty,
//                    Document = r.Payload.ContainsKey("document") ? r.Payload["document"].StringValue : string.Empty,
//                    Score = (float)r.Score,
//                    PointId = r.Id != null ? (r.Id.Uuid ?? r.Id.ToString() ?? string.Empty) : string.Empty
//                })
//                .ToList();
//        }

//        public async Task<List<LocalRagAPI.Models.SearchResultItem>> KeywordSearch(string query, string documentFilter = null, int limit = 20, string userId = null)
//        {
//            var mustConditions = new List<Condition>();

//            // keyword match
//            mustConditions.Add(new Condition
//            {
//                Field = new FieldCondition
//                {
//                    Key = "content",
//                    Match = new Match
//                    {
//                        Text = query
//                    }
//                }
//            });

//            // optional document filter
//            if (!string.IsNullOrEmpty(documentFilter))
//            {
//                mustConditions.Add(new Condition
//                {
//                    Field = new FieldCondition
//                    {
//                        Key = "document",
//                        Match = new Match
//                        {
//                            Keyword = documentFilter
//                        }
//                    }
//                });
//            }

//            // optional user filter
//            if (!string.IsNullOrEmpty(userId))
//            {
//                mustConditions.Add(new Condition
//                {
//                    Field = new FieldCondition
//                    {
//                        Key = "user_id",
//                        Match = new Match { Keyword = userId }
//                    }
//                });
//            }

//            var filter = new Filter { Must = { mustConditions } };

//            var scroll = await _client.ScrollAsync(
//                collectionName: COLLECTION,
//                filter: filter,
//                limit: (uint)limit
//            );

//            return scroll.Result
//                .Select(r => new LocalRagAPI.Models.SearchResultItem
//                {
//                    Content = r.Payload.ContainsKey("content") ? r.Payload["content"].StringValue : string.Empty,
//                    Document = r.Payload.ContainsKey("document") ? r.Payload["document"].StringValue : string.Empty,
//                    Score = 0f,
//                    PointId = r.Id != null ? (r.Id.Uuid ?? r.Id.ToString() ?? string.Empty) : string.Empty
//                })
//                .ToList();
//        }

//        public async Task BatchUpsertAsync(List<PointStruct> points)
//        {
//            if (points == null || !points.Any())
//                return;

//            var sw = System.Diagnostics.Stopwatch.StartNew();
//            try
//            {
//                await _client.UpsertAsync(
//                    COLLECTION,
//                    points
//                );
//            }
//            finally
//            {
//                sw.Stop();
//                _logger?.LogInformation("Upserted {Count} points in {Elapsed}ms", points.Count, sw.ElapsedMilliseconds);
//            }
//        }

//        public async Task<bool> HasPointsAsync(string documentFilter = null, string userId = null)
//        {
//            Filter filter = null;

//            if (!string.IsNullOrEmpty(documentFilter))
//            {
//                filter = new Filter
//                {
//                    Must =
//                    {
//                        new Condition
//                        {
//                            Field = new FieldCondition
//                            {
//                                Key = "document",
//                                Match = new Match
//                                {
//                                    Keyword = documentFilter
//                                }
//                            }
//                        }
//                    }
//                };
//            }

//            if (!string.IsNullOrEmpty(userId))
//            {
//                if (filter == null) filter = new Filter();

//                filter.Must.Add(new Condition
//                {
//                    Field = new FieldCondition
//                    {
//                        Key = "user_id",
//                        Match = new Match { Keyword = userId }
//                    }
//                });
//            }

//            var scroll = await _client.ScrollAsync(
//                collectionName: COLLECTION,
//                filter: filter,
//                limit: (uint)1
//            );

//            return scroll.Result != null && scroll.Result.Any();
//        }

//        public async Task DeleteByDocumentAsync(string documentName)
//        {
//            var filter = new Filter
//            {
//                Must =
//        {
//            new Condition
//            {
//                Field = new FieldCondition
//                {
//                    Key = "document",
//                    Match = new Match
//                    {
//                        Keyword = documentName
//                    }
//                }
//            }
//        }
//            };

//            await _client.DeleteAsync(
//                collectionName: COLLECTION,
//                filter: filter
//            );
//        }

//        public async Task DeleteByDocumentAndUserAsync(string documentName, string userId)
//        {
//            var filter = new Filter
//            {
//                Must =
//                {
//                    new Condition
//                    {
//                        Field = new FieldCondition
//                        {
//                            Key = "document",
//                            Match = new Match { Keyword = documentName }
//                        }
//                    },
//                    new Condition
//                    {
//                        Field = new FieldCondition
//                        {
//                            Key = "user_id",
//                            Match = new Match { Keyword = userId }
//                        }
//                    }
//                }
//            };

//            await _client.DeleteAsync(
//                collectionName: COLLECTION,
//                filter: filter
//            );
//        }
//    }
//}