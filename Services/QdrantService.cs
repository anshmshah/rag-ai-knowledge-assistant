using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace LocalRagAPI.Services
{
    public class QdrantService
    {
        private readonly QdrantClient _client;
        private const string COLLECTION = "documents";

        public QdrantService()
        {
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

        public async Task<List<string>> Search(float[] embedding, string documentFilter = null)
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
                limit: 20,
                filter: filter
            );

            return results
                .Select(r => r.Payload["content"].StringValue)
                .ToList();
        }

        public async Task<List<string>> KeywordSearch(string query, string documentFilter = null)
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
                limit: 20
            );

            return scroll.Result
                .Select(r => r.Payload["content"].StringValue)
                .ToList();
        }
    }
}