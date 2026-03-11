namespace LocalRagAPI.Models
{
    public class DocumentChunk
    {
        public string Content { get; set; }
        public float[] Embedding { get; set; }
        public int KeywordScore { get; set; }

        public string DocumentName { get; set; }
    }
}
