using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LocalRagAPI.Services
{
    public class GroqLLMService : ILLMService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;

        public GroqLLMService(HttpClient httpClient, IConfiguration config)
        {
            _httpClient = httpClient;
            _apiKey = config["Groq:ApiKey"];
        }

        public async Task<string> GenerateResponse(string prompt)
        {
            var requestBody = new
            {
                model = "llama-3.1-8b-instant",   // ✅ safer current model
                messages = new[]
                {
                    new { role = "user", content = prompt }
                },
                temperature = 0.2
            };

            var jsonContent = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json");

            var requestMessage = new HttpRequestMessage(
                HttpMethod.Post,
                "https://api.groq.com/openai/v1/chat/completions");

            requestMessage.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _apiKey);

            requestMessage.Content = jsonContent;

            var response = await _httpClient.SendAsync(requestMessage);

            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return $"Groq API Error: {response.StatusCode} - {responseBody}";
            }

            using var doc = JsonDocument.Parse(responseBody);

            if (!doc.RootElement.TryGetProperty("choices", out var choices))
            {
                return $"Unexpected Groq Response: {responseBody}";
            }

            return choices[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
        }

        public async IAsyncEnumerable<string> StreamResponse(string prompt)
        {
            var requestBody = new
            {
                model = "llama-3.1-8b-instant",
                messages = new[]
                {
            new { role = "user", content = prompt }
        },
                stream = true,
                temperature = 0.1
            };

            var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://api.groq.com/openai/v1/chat/completions");

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _apiKey);

            request.Content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead);

            var stream = await response.Content.ReadAsStreamAsync();
            var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!line.StartsWith("data:"))
                    continue;

                var json = line.Replace("data:", "").Trim();

                if (json == "[DONE]")
                    yield break;

                using var doc = JsonDocument.Parse(json);

                var root = doc.RootElement;

                if (root.TryGetProperty("choices", out var choices))
                {
                    var delta = choices[0]
                        .GetProperty("delta");

                    if (delta.TryGetProperty("content", out var content))
                    {
                        yield return content.GetString();
                    }
                }
            }
        }

    }
}