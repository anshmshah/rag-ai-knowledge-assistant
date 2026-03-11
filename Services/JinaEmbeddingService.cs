using System.Text;
using System.Text.Json;

namespace LocalRagAPI.Services
{
    public class JinaEmbeddingService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;

        public JinaEmbeddingService(HttpClient httpClient, IConfiguration config)
        {
            _httpClient = httpClient;
            _apiKey = config["Jina:ApiKey"];
        }

        public async Task<List<float[]>> GenerateEmbeddings(List<string> inputs)
        {
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://api.jina.ai/v1/embeddings"
            );

            request.Headers.Add("Authorization", $"Bearer {_apiKey}");

            var body = new
            {
                model = "jina-embeddings-v2-base-en",
                input = inputs
            };

            request.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json"
            );

            var response = await _httpClient.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Jina API Error: {json}");
            }

            var result = JsonSerializer.Deserialize<JinaResponse>(json);

            return result.data.Select(x => x.embedding.ToArray()).ToList();
        }

        private class JinaResponse
        {
            public List<JinaEmbedding> data { get; set; }
        }

        private class JinaEmbedding
        {
            public List<float> embedding { get; set; }
        }
    }
}