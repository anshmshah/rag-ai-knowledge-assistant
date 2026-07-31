using System.Text;
using System.Text.Json;
using LocalRagAPI.Models;

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

            _url = config["Qdrant:Url"]?.TrimEnd('/');
            _apiKey = config["Qdrant:ApiKey"];

            if (string.IsNullOrWhiteSpace(_url))
                throw new Exception("Qdrant URL is not configured");

            if (string.IsNullOrWhiteSpace(_apiKey))
                throw new Exception("Qdrant API key is not configured");

            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("api-key", _apiKey);
        }

        public async Task<bool> PingAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var res = await _httpClient.GetAsync($"{_url}/collections", cts.Token);
            return res.IsSuccessStatusCode;
        }

        public async Task InitializeCollection()
        {
            var res = await _httpClient.GetAsync($"{_url}/collections/{COLLECTION}");

            if (!res.IsSuccessStatusCode)
            {
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

                _logger.LogInformation("Qdrant collection '{Collection}' created", COLLECTION);
            }
            else
            {
                _logger.LogInformation("Qdrant collection '{Collection}' already exists", COLLECTION);
            }

            await EnsurePayloadIndexAsync("document", "keyword");
            await EnsurePayloadIndexAsync("user_id", "keyword");
            await EnsurePayloadIndexAsync("document_id", "keyword");
        }

        private async Task EnsurePayloadIndexAsync(string fieldName, string fieldSchema)
        {
            var body = new
            {
                field_name = fieldName,
                field_schema = fieldSchema
            };

            var content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json"
            );

            var response = await _httpClient.PutAsync($"{_url}/collections/{COLLECTION}/index", content);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();

                if (err.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Qdrant payload index already exists for field '{FieldName}'", fieldName);
                    return;
                }

                throw new Exception($"Failed to create payload index for '{fieldName}': {err}");
            }

            _logger.LogInformation("Qdrant payload index ensured for field '{FieldName}'", fieldName);
        }

        public async Task BatchUpsertAsync(List<(string content, string document, float[] vector, string userId, string documentId)> items)
        {
            await InitializeCollection();

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

        public async Task<bool> HasPointsAsync(string? doc = null, string? userId = null)
        {
            await InitializeCollection();

            var body = new
            {
                filter = BuildFilterObject(doc, userId),
                limit = 1,
                with_payload = false,
                with_vector = false
            };

            var content = new StringContent(
                JsonSerializer.Serialize(body, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }),
                Encoding.UTF8,
                "application/json"
            );

            var res = await _httpClient.PostAsync($"{_url}/collections/{COLLECTION}/points/scroll", content);

            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync();
                throw new Exception($"HasPoints failed: {err}");
            }

            var json = await res.Content.ReadAsStringAsync();
            using var docJson = JsonDocument.Parse(json);

            if (!docJson.RootElement.TryGetProperty("result", out var result))
                return false;

            if (!result.TryGetProperty("points", out var points))
                return false;

            return points.ValueKind == JsonValueKind.Array && points.GetArrayLength() > 0;
        }

        public async Task<List<SearchResultItem>> Search(float[] embedding, string? doc = null, int limit = 20, string? userId = null)
        {
            await InitializeCollection();

            var body = new
            {
                vector = embedding,
                limit = limit,
                with_payload = true,
                with_vector = false,
                filter = BuildFilterObject(doc, userId)
            };

            var content = new StringContent(
                JsonSerializer.Serialize(body, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }),
                Encoding.UTF8,
                "application/json"
            );

            var res = await _httpClient.PostAsync($"{_url}/collections/{COLLECTION}/points/search", content);

            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync();
                throw new Exception($"Search failed: {err}");
            }

            var json = await res.Content.ReadAsStringAsync();
            using var docJson = JsonDocument.Parse(json);

            var output = new List<SearchResultItem>();

            if (!docJson.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
                return output;

            foreach (var item in result.EnumerateArray())
            {
                string contentText = "";
                string documentName = "";
                float score = 0;
                string pointId = "";

                if (item.TryGetProperty("payload", out var payload))
                {
                    if (payload.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
                        contentText = contentProp.GetString() ?? "";

                    if (payload.TryGetProperty("document", out var documentProp) && documentProp.ValueKind == JsonValueKind.String)
                        documentName = documentProp.GetString() ?? "";
                }

                if (item.TryGetProperty("score", out var scoreProp))
                {
                    if (scoreProp.ValueKind == JsonValueKind.Number)
                        score = scoreProp.GetSingle();
                }

                if (item.TryGetProperty("id", out var idProp))
                {
                    if (idProp.ValueKind == JsonValueKind.String)
                        pointId = idProp.GetString() ?? "";
                    else
                        pointId = idProp.ToString();
                }

                output.Add(new SearchResultItem
                {
                    Content = contentText,
                    Document = documentName,
                    Score = score,
                    PointId = pointId
                });
            }

            return output;
        }

        public async Task<List<SearchResultItem>> KeywordSearch(string query, string? doc = null, int limit = 20, string? userId = null)
        {
            await InitializeCollection();

            var must = new List<object>();

            if (!string.IsNullOrWhiteSpace(doc))
            {
                must.Add(new
                {
                    key = "document",
                    match = new { value = doc }
                });
            }

            if (!string.IsNullOrWhiteSpace(userId))
            {
                must.Add(new
                {
                    key = "user_id",
                    match = new { value = userId }
                });
            }

            var body = new
            {
                filter = must.Count > 0 ? new { must } : null,
                limit = limit,
                with_payload = true,
                with_vector = false
            };

            var content = new StringContent(
                JsonSerializer.Serialize(body, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }),
                Encoding.UTF8,
                "application/json"
            );

            var res = await _httpClient.PostAsync($"{_url}/collections/{COLLECTION}/points/scroll", content);

            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync();
                throw new Exception($"KeywordSearch failed: {err}");
            }

            var json = await res.Content.ReadAsStringAsync();
            using var docJson = JsonDocument.Parse(json);

            var output = new List<SearchResultItem>();

            if (!docJson.RootElement.TryGetProperty("result", out var result))
                return output;

            if (!result.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array)
                return output;

            foreach (var item in points.EnumerateArray())
            {
                string contentText = "";
                string documentName = "";
                string pointId = "";

                if (item.TryGetProperty("payload", out var payload))
                {
                    if (payload.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
                        contentText = contentProp.GetString() ?? "";

                    if (payload.TryGetProperty("document", out var documentProp) && documentProp.ValueKind == JsonValueKind.String)
                        documentName = documentProp.GetString() ?? "";
                }

                if (!string.IsNullOrWhiteSpace(query) &&
                    !contentText.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (item.TryGetProperty("id", out var idProp))
                {
                    if (idProp.ValueKind == JsonValueKind.String)
                        pointId = idProp.GetString() ?? "";
                    else
                        pointId = idProp.ToString();
                }

                output.Add(new SearchResultItem
                {
                    Content = contentText,
                    Document = documentName,
                    Score = 0,
                    PointId = pointId
                });
            }

            return output;
        }

        public Task DeleteByDocumentAsync(string name)
        {
            return DeleteByFilterAsync(name, null);
        }

        public Task DeleteByDocumentAndUserAsync(string name, string userId)
        {
            return DeleteByFilterAsync(name, userId);
        }

        public Task DeleteByDocumentIdAsync(string documentId)
        {
            return DeleteByFilterAsync(null, null, documentId);
        }

        private async Task DeleteByFilterAsync(string? doc, string? userId, string? documentId = null)
        {
            await InitializeCollection();

            var body = new
            {
                filter = BuildFilterObject(doc, userId, documentId)
            };

            var content = new StringContent(
                JsonSerializer.Serialize(body, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }),
                Encoding.UTF8,
                "application/json"
            );

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_url}/collections/{COLLECTION}/points/delete")
            {
                Content = content
            };

            var res = await _httpClient.SendAsync(request);

            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync();
                throw new Exception($"Delete failed: {err}");
            }

            _logger.LogInformation("Deleted Qdrant points for doc={Doc} userId={UserId} documentId={DocumentId}", doc, userId, documentId);
        }

        private object? BuildFilterObject(string? doc, string? userId, string? documentId = null)
        {
            var must = new List<object>();

            if (!string.IsNullOrWhiteSpace(doc))
            {
                must.Add(new
                {
                    key = "document",
                    match = new { value = doc }
                });
            }

            if (!string.IsNullOrWhiteSpace(userId))
            {
                must.Add(new
                {
                    key = "user_id",
                    match = new { value = userId }
                });
            }

            if (!string.IsNullOrWhiteSpace(documentId))
            {
                must.Add(new
                {
                    key = "document_id",
                    match = new { value = documentId }
                });
            }

            if (must.Count == 0)
                return null;

            return new { must };
        }
    }
}

