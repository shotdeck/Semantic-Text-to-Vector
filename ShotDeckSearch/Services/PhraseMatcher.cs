using System.Text.RegularExpressions;
using System.Linq;

namespace ShotDeckSearch.Services
{
    public class PhraseMatcher
    {
        private readonly HashSet<string> _phrases;
        private readonly int _maxWordsInPhrase;

        public PhraseMatcher(IEnumerable<string> phrases)
        {
            _phrases = new HashSet<string>(
                phrases.Select(p => Normalize(p)),
                StringComparer.OrdinalIgnoreCase
            );

            _maxWordsInPhrase = _phrases
                .Select(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)
                .DefaultIfEmpty(1)
                .Max();
        }

        public List<string> FindMatches(string sentence)
        {
            var tokens = Tokenize(sentence);
            var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < tokens.Length; i++)
            {
                for (int length = 1; length <= _maxWordsInPhrase && i + length <= tokens.Length; length++)
                {
                    var window = string.Join(" ", tokens.Skip(i).Take(length));
                    if (_phrases.Contains(window))
                    {
                        matches.Add(window);
                    }
                }
            }

            return matches.ToList();
        }

        private static string Normalize(string input) =>
            Regex.Replace(input.ToLowerInvariant(), @"[^\w\s]", "").Trim();

        private static string[] Tokenize(string input) =>
            Normalize(input).Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}