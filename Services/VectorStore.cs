using LocalRagAPI.Models;

namespace LocalRagAPI.Services
{
    public class VectorStore
    {
        public List<DocumentChunk> Chunks { get; } = new();

        public static float CosineSimilarity(float[] a, float[] b)
        {
            float dot = 0;
            float normA = 0;
            float normB = 0;

            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }

            return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
        }
    }
}
