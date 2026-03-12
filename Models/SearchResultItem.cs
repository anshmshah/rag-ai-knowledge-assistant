namespace LocalRagAPI.Models
{
    public class SearchResultItem
    {
        public string Content { get; set; }
        public float Score { get; set; }
        public string Document { get; set; }
        public string PointId { get; set; }
    }
}
