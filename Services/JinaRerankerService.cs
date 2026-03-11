using System.Text;
using System.Text.Json;

namespace LocalRagAPI.Services
{
    public class JinaRerankerService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;

        public JinaRerankerService(HttpClient httpClient, IConfiguration config)
        {
            _httpClient = httpClient;
            _apiKey = config["Jina:ApiKey"];
        }

        public async Task<List<string>> Rerank(string query, List<string> documents)
        {
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://api.jina.ai/v1/rerank"
            );

            request.Headers.Add("Authorization", $"Bearer {_apiKey}");

            var body = new
            {
                model = "jina-reranker-v1-base-en",
                query = query,
                documents = documents
            };

            request.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json"
            );

            var response = await _httpClient.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            var result = JsonSerializer.Deserialize<RerankResponse>(json);

            return result.results
                .Where(r => r.relevance_score > 0.1)
                .OrderByDescending(r => r.relevance_score)
                .Select(r => documents[r.index])
                .Take(5)
                .ToList();
        }

        private class RerankResponse
        {
            public List<RerankItem> results { get; set; }
        }

        private class RerankItem
        {
            public int index { get; set; }
            public float relevance_score { get; set; }
        }
    }
}