using System.Text;
using System.Text.Json;

namespace LocalRagAPI.Services
{
    public class JinaEmbeddingService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly Microsoft.Extensions.Logging.ILogger<JinaEmbeddingService> _logger;

        public JinaEmbeddingService(HttpClient httpClient, IConfiguration config, Microsoft.Extensions.Logging.ILogger<JinaEmbeddingService> logger)
        {
            _httpClient = httpClient;
            _apiKey = config["Jina:ApiKey"];
            _logger = logger;
        }

        public async Task<List<float[]>> GenerateEmbeddings(List<string> inputs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Retry logic for transient failures
            int maxAttempts = 3;
            Exception lastEx = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
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
                        _logger?.LogWarning("Jina API returned non-success status. Attempt {Attempt}/{Max}: {Status} {Body}", attempt, maxAttempts, response.StatusCode, json);
                        lastEx = new Exception($"Jina API Error: {json}");
                    }
                    else
                    {
                        var result = JsonSerializer.Deserialize<JinaResponse>(json);

                        sw.Stop();
                        _logger?.LogInformation("Generated {Count} embeddings in {Elapsed}ms (attempt {Attempt})", inputs.Count, sw.ElapsedMilliseconds, attempt);

                        return result.data.Select(x => x.embedding.ToArray()).ToList();
                    }
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    _logger?.LogWarning(ex, "Jina embedding call failed on attempt {Attempt}/{Max}", attempt, maxAttempts);
                }

                // exponential backoff before retrying
                await Task.Delay( (int)(Math.Pow(2, attempt) * 250) );
            }

            // If we reach here all attempts failed
            _logger?.LogError(lastEx, "Jina embedding generation failed after {Max} attempts", 3);
            throw lastEx ?? new Exception("Jina embedding generation failed");
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