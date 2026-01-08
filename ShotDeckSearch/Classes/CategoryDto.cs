namespace ShotDeckSearch.Classes
{
    public class CategoryDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = default!;
        public List<KeywordDto> Keywords { get; set; } = new();
    }

    public sealed class KeywordDto
    {
        public int Id { get; set; }
        public string Keyword { get; set; } = default!;
        public bool? IsIncluded { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }
    }
}
