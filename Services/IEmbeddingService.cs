namespace LocalRagAPI.Services
{
    public interface IEmbeddingService
    {
        Task<List<float[]>> GenerateEmbeddings(List<string> inputs);
    }
}
