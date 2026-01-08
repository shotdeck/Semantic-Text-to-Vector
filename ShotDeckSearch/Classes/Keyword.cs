namespace ShotDeckSearch.Classes
{
    public class Keyword
    {
        public int Id { get; set; }

        public string KeywordText { get; set; } = string.Empty;

        public int CategoryId { get; set; }

        public bool IsIncluded { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigation property (optional if you're not using EF)
        public KeywordCategory? Category { get; set; }
    }

}
