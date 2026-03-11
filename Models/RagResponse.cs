namespace LocalRagAPI.Models
{
    public class RagResponse
    {
        public string Answer { get; set; }

        public List<string> Sources { get; set; } = new();
    }
}
