using Qdrant.Client;
using Qdrant.Client.Grpc;
using Microsoft.Extensions.Configuration;

namespace LocalRagAPI.Services
{
    public class QdrantService
    {
        private readonly QdrantClient _client;
        private const string COLLECTION = "documents";
        private readonly Microsoft.Extensions.Logging.ILogger<QdrantService> _logger;
        private readonly bool _recreateOnStartup;

        public QdrantService(Microsoft.Extensions.Logging.ILogger<QdrantService> logger, IConfiguration config)
        {
            _logger = logger;
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            // Read optional configuration for Qdrant connection and startup behavior
            var host = config["Qdrant:Host"] ?? "localhost";
            var port = 6334;
            if (int.TryParse(config["Qdrant:Port"], out var p)) port = p;

            _recreateOnStartup = config.GetValue<bool>("Qdrant:RecreateOnStartup", false);

            _client = new QdrantClient(host, port);
        }

        // =========================
        // CREATE COLLECTION
        // =========================

        public async Task InitializeCollection()
        {
            var collections = await _client.ListCollectionsAsync();

            if (collections.Contains(COLLECTION))
            {
                if (_recreateOnStartup)
                {
                    _logger?.LogWarning("Qdrant collection '{Collection}' exists and will be recreated because Qdrant:RecreateOnStartup=true", COLLECTION);
                    await _client.DeleteCollectionAsync(COLLECTION);

                    await _client.CreateCollectionAsync(
                        COLLECTION,
                        new VectorParams
                        {
                            Size = 768,
                            Distance = Distance.Cosine
                        });
                }
                else
                {
                    _logger?.LogInformation("Qdrant collection '{Collection}' already exists; skipping creation.", COLLECTION);
                }
            }
            else
            {
                _logger?.LogInformation("Qdrant collection '{Collection}' does not exist and will be created.", COLLECTION);
                await _client.CreateCollectionAsync(
                    COLLECTION,
                    new VectorParams
                    {
                        Size = 768,
                        Distance = Distance.Cosine
                    });
            }
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

        public async Task<List<LocalRagAPI.Models.SearchResultItem>> Search(float[] embedding, string documentFilter = null, int limit = 20, string userId = null)
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

            // If userId provided, add user filter
            if (!string.IsNullOrEmpty(userId))
            {
                if (filter == null) filter = new Filter();

                filter.Must.Add(new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "user_id",
                        Match = new Match { Keyword = userId }
                    }
                });
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

        public async Task<List<LocalRagAPI.Models.SearchResultItem>> KeywordSearch(string query, string documentFilter = null, int limit = 20, string userId = null)
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

            // optional user filter
            if (!string.IsNullOrEmpty(userId))
            {
                mustConditions.Add(new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "user_id",
                        Match = new Match { Keyword = userId }
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

        public async Task<bool> HasPointsAsync(string documentFilter = null, string userId = null)
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

            if (!string.IsNullOrEmpty(userId))
            {
                if (filter == null) filter = new Filter();

                filter.Must.Add(new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "user_id",
                        Match = new Match { Keyword = userId }
                    }
                });
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

        public async Task DeleteByDocumentAndUserAsync(string documentName, string userId)
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
                            Match = new Match { Keyword = documentName }
                        }
                    },
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "user_id",
                            Match = new Match { Keyword = userId }
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