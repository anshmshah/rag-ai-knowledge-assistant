namespace LocalRagAPI.Services
{
    public interface ILLMService
    {
        Task<string> GenerateResponse(string prompt);

        IAsyncEnumerable<string> StreamResponse(string prompt);
    }
}
