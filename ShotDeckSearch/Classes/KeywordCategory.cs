using static Microsoft.Extensions.Logging.EventSource.LoggingEventSource;

namespace ShotDeckSearch.Classes
{
    public class KeywordCategory
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public List<Keyword> Keywords { get; set; } = new();
    }

}
