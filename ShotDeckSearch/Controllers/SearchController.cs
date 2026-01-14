// SearchController.cs  (FULL FILE)
// ✅ Changes:
//  - Single-word PERSON keywords (Director/Actors/Cine/Prod/Costume) are ignored unless a nearby cue exists
//  - Fixes "A kid ... directed by kid" by evaluating ALL occurrences of the keyword (not just first IndexOf)
//  - Adds cues for cinematography / production design / costume design (optional but included)

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using ShotDeck.Keywords;
using ShotDeckSearch.Helpers;
using ShotDeckSearch.Models;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ShotDeckSearch.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class SearchController : ControllerBase
    {
        private readonly NpgsqlConnection _connection;
        private readonly IConfiguration _configuration;
        private readonly IKeywordCacheService _keywordCache;
        private readonly ILogger<SearchController> _logger;

        private sealed record KeywordResult(string Keyword, List<string> Categories);

        public SearchController(
            NpgsqlConnection connection,
            IConfiguration configuration,
            IKeywordCacheService keywordCache,
            ILogger<SearchController> logger)
        {
            _connection = connection;
            _configuration = configuration;
            _keywordCache = keywordCache;
            _logger = logger;
        }

        /// <summary>
        /// Checks whether the supplied password matches the configured password.
        /// </summary>
        /// <param name="password">Password to validate</param>
        /// <returns>true if valid, false otherwise</returns>
        [HttpGet("check-password")]
        public ActionResult<bool> CheckPassword([FromQuery] string password)
        {
            if (string.IsNullOrEmpty(password))
                return false;

            var configuredPassword = _configuration["Password"];

            if (string.IsNullOrEmpty(configuredPassword))
                return false;

            return password == configuredPassword;
        }



        [HttpGet("word-in-keywords")]
        public ActionResult<List<SearchHitDto>> GetWordInKeywords(string phrase)
        {
            try
            {
                var hits = _keywordCache
                    .SearchWithSources(phrase)
                    .Select(h => new SearchHitDto
                    {
                        Keyword = h.Keyword,
                        Sources = h.Sources?.ToList() ?? new List<string>()
                    })
                    .ToList();

                return Ok(hits);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Get word-in-keywords");

#if DEBUG
                return StatusCode(500, new { message = ex.Message, stack = ex.StackTrace });
#else
                return StatusCode(500, "An unexpected error occurred.");
#endif
            }
        }

        [HttpGet("word-by-source")]
        public ActionResult<List<string>> GetWordBySource([FromQuery] string source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return BadRequest("Source is required.");

            var q = source.Trim();

            var results = _keywordCache
                .GetKeywordSources()
                .Where(kvp => kvp.Value.Any(s =>
                    s.Equals(q, StringComparison.OrdinalIgnoreCase) ||
                    s.StartsWith(q + ":", StringComparison.OrdinalIgnoreCase)))
                .Select(kvp => kvp.Key)
                .OrderBy(k => k)
                .ToList();

            return Ok(results);
        }

        [HttpGet("categories")]
        public ActionResult<List<string>> GetCategories()
        {
            var results = _keywordCache
                .GetKeywordSources()
                .Select(kvp => kvp.Key)
                .OrderBy(k => k)
                .ToList();

            return Ok(results);
        }

        [HttpGet("extractkeywords")]
        public IActionResult GetExtractKeywords([FromQuery] string prompt)
        {
            var matchedKeywords = _keywordCache.Search(prompt);
            var result = KeywordParser.ClassifyMatchedKeywords(prompt, matchedKeywords);

            return Ok(new
            {
                include = result.Include,
                exclude = result.Exclude
            });
        }

        [HttpGet("status")]
        public ActionResult<Models.KeywordCacheStatus> GetStatus()
            => Ok((_keywordCache as KeywordCacheService)?.GetKeywordCacheStatus());

        [HttpPost("diagnose-categories")]
        public async Task<IActionResult> DiagnoseCategories([FromBody] List<string> terms)
        {
            if (terms == null || terms.Count == 0)
                return Ok(new { results = new object[0] });

            var byCategory = _keywordCache.GetKeywordsByCategory();
            var flat = _keywordCache.GetFlatKeywordSet();

            // Build keyword → categories map
            var kwToCats = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (cat, kws) in byCategory)
            {
                foreach (var kw in kws)
                {
                    if (!kwToCats.TryGetValue(kw, out var set))
                        set = kwToCats[kw] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(cat);
                }
            }

            var results = new List<object>();

            foreach (var rawTerm in terms)
            {
                var term = rawTerm?.Trim();
                if (string.IsNullOrWhiteSpace(term))
                    continue;

                string? matchedKeyword = null;

                // 1) Exact match in flat keyword list
                matchedKeyword = flat.FirstOrDefault(k =>
                    k.Equals(term, StringComparison.OrdinalIgnoreCase));

                // 2) PhraseMatcher search if not found
                if (matchedKeyword == null)
                {
                    var hits = _keywordCache.Search(term);
                    if (hits.Count > 0)
                        matchedKeyword = hits.OrderByDescending(h => h.Length).First();
                }

                // 3) If still not found, fallback to DB search in frl_join_images_tags
                bool foundInTags = false;
                if (matchedKeyword == null)
                {
                    var sql = @"SELECT tag FROM frl_join_images_tags WHERE tag ILIKE @tag LIMIT 1;";
                    using var cmd = new NpgsqlCommand(sql, _connection);
                    cmd.Parameters.AddWithValue("tag", term);

                    var db = await cmd.ExecuteScalarAsync();
                    if (db != null)
                    {
                        matchedKeyword = db.ToString();
                        foundInTags = true;
                    }
                }

                // 4) Build category list
                List<string> categories =
                    matchedKeyword != null && kwToCats.TryGetValue(matchedKeyword, out var set2)
                        ? set2.OrderBy(x => x).ToList()
                        : new List<string>();

                if (foundInTags)
                    categories.Add("Image Tag");

                results.Add(new
                {
                    term,
                    keyword = matchedKeyword,
                    categories,
                    found = matchedKeyword != null,
                    source = foundInTags ? "frl_join_images_tags" : "keyword_cache"
                });
            }

            return Ok(new { results });
        }

        [HttpGet("extractkeywordandcategories")]
        public IActionResult GetExtractKeywordsAndCategories([FromQuery] string prompt)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    return Ok(new
                    {
                        include = Array.Empty<object>(),
                        exclude = Array.Empty<object>(),
                        search = string.Empty,
                        rejectedForCensorship = false,
                        superBlackList = false
                    });
                }

                prompt = prompt
     .Replace("\"", "")
     .Replace("'", "")
     .Replace("“", "")
     .Replace("”", "")
     .Replace("‘", "")
     .Replace("’", "");


                var rawPrompt = prompt;

                var censorshipResult = _keywordCache.CheckCensorship(rawPrompt);
                if (censorshipResult.RejectedForCensorship)
                {
                    _logger.LogWarning(
                        "EKAC censorship rejection: prompt='{prompt}', matchedWord='{matchedWord}', superBlackList={superBlackList}",
                        rawPrompt, censorshipResult.MatchedWord, censorshipResult.SuperBlackList);

                    return Ok(new
                    {
                        include = Array.Empty<object>(),
                        exclude = Array.Empty<object>(),
                        search = prompt,
                        rejectedForCensorship = true,
                        superBlackList = censorshipResult.SuperBlackList
                    });
                }

                // 0) Cue index
                var cueInfo = BuildCueIndex(rawPrompt);

                // 1) Match keywords (may include synonyms)
                var matched = _keywordCache.Search(rawPrompt);

                _logger.LogInformation("EKAC rawPrompt='{rawPrompt}'", rawPrompt);
                _logger.LogInformation("EKAC matchedCount={count} matched={matched}",
                    matched.Count,
                    string.Join(" | ", matched));

                // 1b) Explicit movie title extraction
                var forcedMovieTitle = ExtractExplicitMovieTitle(rawPrompt);
                if (!string.IsNullOrWhiteSpace(forcedMovieTitle))
                    forcedMovieTitle = _keywordCache.Canonicalize(forcedMovieTitle);

                if (!string.IsNullOrWhiteSpace(forcedMovieTitle) &&
                    !matched.Contains(forcedMovieTitle, StringComparer.OrdinalIgnoreCase))
                {
                    matched.Add(forcedMovieTitle);
                }

                // 1c) Classify include/exclude
                var classified = KeywordParser.ClassifyMatchedKeywords(rawPrompt, matched);

                // Restore any matched keywords the parser dropped
                var includeCanon = new HashSet<string>(
                    classified.Include.Select(k => _keywordCache.Canonicalize(k)),
                    StringComparer.OrdinalIgnoreCase);

                var excludeCanon = new HashSet<string>(
                    classified.Exclude.Select(k => _keywordCache.Canonicalize(k)),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var m in matched)
                {
                    if (string.IsNullOrWhiteSpace(m)) continue;

                    var cm = _keywordCache.Canonicalize(m);
                    if (!includeCanon.Contains(cm) && !excludeCanon.Contains(cm))
                    {
                        classified.Include.Add(m);
                        includeCanon.Add(cm);
                    }
                }

                _logger.LogInformation(
                    "EKAC classified includeCount={ic} excludeCount={ec} include={inc} exclude={exc}",
                    classified.Include.Count,
                    classified.Exclude.Count,
                    string.Join(" | ", classified.Include),
                    string.Join(" | ", classified.Exclude));

                // 2) keyword -> categories map
                var byCategory = _keywordCache.GetKeywordsByCategory();
                var kwToCats = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                foreach (var kv in byCategory)
                {
                    foreach (var kw in kv.Value ?? Enumerable.Empty<string>())
                    {
                        if (!kwToCats.TryGetValue(kw, out var set))
                            set = kwToCats[kw] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        set.Add(kv.Key);
                    }
                }


                // 3) Canonicalize include/exclude for output
                List<string> CanonicalizeList(IEnumerable<string> list) =>
                    list.Select(k => _keywordCache.Canonicalize(k))
                        .Where(k => !string.IsNullOrWhiteSpace(k))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                var includeKeys = CanonicalizeList(classified.Include);
                var excludeKeys = CanonicalizeList(classified.Exclude);

                // 4) Pick nearest Title keyword (only AFTER title cue)
                string? nearestTitleKeyword = null;
                int nearestDistance = int.MaxValue;

                var allKeywords = includeKeys
                    .Concat(excludeKeys)
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var kw in allKeywords)
                {
                    if (!string.IsNullOrWhiteSpace(forcedMovieTitle) &&
                        kw.Equals(forcedMovieTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        nearestTitleKeyword = kw;
                        nearestDistance = 0;
                        continue;
                    }

                    var (_, _, _, _, _, wantTitle) = GetLocalIntentForKeyword(rawPrompt, cueInfo, kw);
                    if (!wantTitle) continue;

                    // Find best occurrence (after title cue) by scanning occurrences
                    var occurrences = FindAllWholeWordPositions(cueInfo.Lower, kw);

                    foreach (var charPos in occurrences)
                    {
                        int kwTok = CharIndexToTokenIndex(charPos, cueInfo.CharToToken);
                        if (kwTok < 0) continue;

                        foreach (var tpos in cueInfo.TitleTokenPositions)
                        {
                            int signed = kwTok - tpos;
                            if (signed < 0) continue;

                            if (signed < nearestDistance)
                            {
                                nearestDistance = signed;
                                nearestTitleKeyword = kw;
                            }
                        }
                    }
                }

                KeywordResult? Shape(string k)
                {
                    if (string.IsNullOrWhiteSpace(k)) return null;

                    var key = k.Trim();

                    // -----------------------------------------
                    // 1) Build category set from keyword cache
                    // -----------------------------------------
                    var catSet = kwToCats.TryGetValue(key, out var set)
                        ? new HashSet<string>(set, StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    // Add categories derived from sources (skip synonym:* sources)
                    var sources = _keywordCache.GetSourcesFor(key);
                    foreach (var src in sources)
                    {
                        if (src.StartsWith("synonym:", StringComparison.OrdinalIgnoreCase)) continue;
                        if (src.Equals("synonym_master", StringComparison.OrdinalIgnoreCase)) continue;
                        catSet.Add(MapSourceLabelToCategory(src));
                    }

                    // If the canonical term only has "synonym_master" source (pure synonym master),
                    // also look up sources from its synonyms to derive categories
                    if (catSet.Count == 0)
                    {
                        var syns = _keywordCache.GetSynonymsForMaster(key);
                        foreach (var syn in syns)
                        {
                            if (string.IsNullOrWhiteSpace(syn)) continue;
                            var synSources = _keywordCache.GetSourcesFor(syn);
                            foreach (var src in synSources)
                            {
                                // Skip the synonym:master reference itself
                                if (src.StartsWith("synonym:", StringComparison.OrdinalIgnoreCase)) continue;
                                if (src.Equals("synonym_master", StringComparison.OrdinalIgnoreCase)) continue;
                                catSet.Add(MapSourceLabelToCategory(src));
                            }
                        }
                    }

                    // If still no categories but this is a valid synonym master, add a default "Keywords" category
                    // so the keyword is not filtered out
                    if (catSet.Count == 0 && sources.Any(s => s.Equals("synonym_master", StringComparison.OrdinalIgnoreCase)))
                    {
                        catSet.Add("Keywords");
                    }

                    // -----------------------------------------
                    // 2) Local intent detection (cue proximity)
                    // -----------------------------------------
                    // Use the term that actually appears in the prompt for intent detection.
                    // If the canonical key isn't present, try its synonyms and pick the first match found.
                    string intentTerm = key;

                    if (!string.IsNullOrWhiteSpace(rawPrompt))
                    {
                        var lowerPrompt = cueInfo.Lower; // already lower-cased prompt

                        if (lowerPrompt.IndexOf(key.ToLowerInvariant(), StringComparison.Ordinal) < 0)
                        {
                            var syns = _keywordCache.GetSynonymsForMaster(key);
                            foreach (var syn in syns)
                            {
                                if (string.IsNullOrWhiteSpace(syn)) continue;

                                if (lowerPrompt.IndexOf(syn.ToLowerInvariant(), StringComparison.Ordinal) >= 0)
                                {
                                    intentTerm = syn;
                                    break;
                                }
                            }
                        }
                    }

                    var (wantDirector, wantActing, wantCine, wantProd, wantCostume, wantTitle) =
                        GetLocalIntentForKeyword(rawPrompt, cueInfo, intentTerm);

                    // Forced title extracted via regex
                    bool isForcedTitle = !string.IsNullOrWhiteSpace(forcedMovieTitle) &&
                                         key.Equals(forcedMovieTitle, StringComparison.OrdinalIgnoreCase);

                    if (isForcedTitle) wantTitle = true;

                    // Nearest title keyword (only one keyword keeps Title)
                    bool isNearestTitle = nearestTitleKeyword != null &&
                                          nearestTitleKeyword.Equals(key, StringComparison.OrdinalIgnoreCase);

                    // -----------------------------------------
                    // 3) Apply PERSON / CREW intent filtering
                    // (director suppresses cinematographer, etc.)
                    // -----------------------------------------
                    if (wantDirector || wantActing || wantCine || wantProd || wantCostume)
                    {
                        ApplyIntentFilter(catSet, wantDirector, wantActing, wantCine, wantProd, wantCostume);
                    }

                    // -----------------------------------------
                    // 4) Title filtering (existing logic)
                    // -----------------------------------------
                    bool isSynonymMaster = sources.Any(s => s.Equals("synonym_master", StringComparison.OrdinalIgnoreCase));
                    bool hadTitleFromSources = sources.Any(s =>
                        s.Equals("movie:title", StringComparison.OrdinalIgnoreCase) ||
                        s.Equals("title", StringComparison.OrdinalIgnoreCase));

                    if (catSet.Contains("Title"))
                    {
                        // Don't filter out Title for synonym masters that have Title as their source
                        // This ensures movie title synonyms are returned even without a title cue
                        if (isSynonymMaster && hadTitleFromSources)
                        {
                            // Keep Title category for synonym masters with title source
                            ApplyTitleFilter(catSet, keepTitle: true);
                        }
                        else if (!wantTitle || !isNearestTitle)
                            ApplyTitleFilter(catSet, keepTitle: false);
                        else
                            ApplyTitleFilter(catSet, keepTitle: true);
                    }

                    // -----------------------------------------
                    // 5) Ignore single-word PERSON names unless cued
                    // ("a kid holding a balloon")
                    // -----------------------------------------
                    if (IsSingleWordName(key))
                    {
                        bool hasPersonBucket = catSet.Any(c =>
                            c.Equals("Director", StringComparison.OrdinalIgnoreCase) ||
                            c.Equals("Actors", StringComparison.OrdinalIgnoreCase) ||
                            c.Equals("Actor", StringComparison.OrdinalIgnoreCase) ||
                            c.Equals("Cast", StringComparison.OrdinalIgnoreCase) ||
                            c.IndexOf("actor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            c.IndexOf("cast", StringComparison.OrdinalIgnoreCase) >= 0);

                        if (hasPersonBucket && !isSynonymMaster && !(wantDirector || wantActing || wantCine || wantProd || wantCostume))
                        {
                            var toRemove = catSet
                                .Where(c =>
                                    c.Equals("Director", StringComparison.OrdinalIgnoreCase) ||
                                    c.Equals("Actors", StringComparison.OrdinalIgnoreCase) ||
                                    c.Equals("Actor", StringComparison.OrdinalIgnoreCase) ||
                                    c.Equals("Cast", StringComparison.OrdinalIgnoreCase) ||
                                    c.IndexOf("actor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    c.IndexOf("cast", StringComparison.OrdinalIgnoreCase) >= 0)
                                .ToList();

                            foreach (var c in toRemove)
                                catSet.Remove(c);
                        }
                    }

                    // -----------------------------------------
                    // 6) Ignore single-word mv_artist / comm_brand
                    // unless used as a TITLE
                    // ("a ghost on a beach")
                    // -----------------------------------------
                    if (IsSingleWordName(key))
                    {
                        bool hasAmbiguousNameBucket = catSet.Any(c =>
                            c.Equals("mv_artist", StringComparison.OrdinalIgnoreCase) ||
                            c.Equals("Music Video Artist", StringComparison.OrdinalIgnoreCase) ||
                            c.Equals("comm_brand", StringComparison.OrdinalIgnoreCase) ||
                            c.Equals("Commercial Brand", StringComparison.OrdinalIgnoreCase));

                        if (hasAmbiguousNameBucket)
                        {
                            bool keepBecauseTitle = wantTitle && isNearestTitle;

                            if (!keepBecauseTitle)
                            {
                                var toRemove = catSet
                                    .Where(c =>
                                        c.Equals("mv_artist", StringComparison.OrdinalIgnoreCase) ||
                                        c.Equals("Music Video Artist", StringComparison.OrdinalIgnoreCase) ||
                                        c.Equals("comm_brand", StringComparison.OrdinalIgnoreCase) ||
                                        c.Equals("Commercial Brand", StringComparison.OrdinalIgnoreCase))
                                    .ToList();

                                foreach (var c in toRemove)
                                    catSet.Remove(c);
                            }
                        }
                    }

                    // -----------------------------------------
                    // 7) Final output
                    // -----------------------------------------
                    if (catSet.Count == 0) return null;

                    return new KeywordResult(key, catSet.OrderBy(x => x).ToList());
                }

                var includeResults = includeKeys
                    .Select(Shape)
                    .Where(r => r != null)
                    .Cast<KeywordResult>()
                    .ToList();

                var excludeResults = excludeKeys
                    .Select(Shape)
                    .Where(r => r != null)
                    .Cast<KeywordResult>()
                    .ToList();

                // Search text removal (canonical + raw terms that mapped to returned canonicals)
                var returnedCanon = new HashSet<string>(
                    includeResults.Select(r => r.Keyword).Concat(excludeResults.Select(r => r.Keyword)),
                    StringComparer.OrdinalIgnoreCase);

                var rawTermsToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var raw in classified.Include.Concat(classified.Exclude))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    var canonical = _keywordCache.Canonicalize(raw);
                    if (returnedCanon.Contains(canonical))
                        rawTermsToRemove.Add(raw);
                }

                var allTermsToRemove = returnedCanon
                    .Concat(rawTermsToRemove)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(x => new KeywordResult(x, new List<string>()))
                    .ToList();

                var search = ExtractDescription(rawPrompt, allTermsToRemove, Array.Empty<KeywordResult>());

                var include = includeResults
                    .Select(r => new { keyword = r.Keyword, categories = r.Categories })
                    .ToList();

                var exclude = excludeResults
                    .Select(r => new { keyword = r.Keyword, categories = r.Categories })
                    .ToList();

                return Ok(new { include, exclude, search, rejectedForCensorship = false, superBlackList = false });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in extractkeywordandcategories");

#if DEBUG
                return StatusCode(500, new { message = ex.Message, stack = ex.StackTrace });
#else
        return StatusCode(500, "An unexpected error occurred.");
#endif
            }
        }


        // ---------------- helpers (controller-local) ----------------

        private static bool IsSingleWordName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            return !s.Any(char.IsWhiteSpace);
        }

        private static readonly HashSet<string> AmbiguousNameCategories = new(StringComparer.OrdinalIgnoreCase)
{
    // These are the “one-word name collisions” buckets
    "mv_artist",
    "Music Video Artist",
    "comm_brand",
    "Commercial Brand"
};

        private static void RemoveAmbiguousNameCategories(HashSet<string> catSet)
        {
            var toRemove = catSet.Where(c => AmbiguousNameCategories.Contains(c)).ToList();
            foreach (var c in toRemove)
                catSet.Remove(c);
        }

       


        private static readonly HashSet<string> PersonCategories = new(StringComparer.OrdinalIgnoreCase)
        {
            "Director",
            "Actors",
            "Actor",
            "Cast",
            "Cinematographer",
            "Production Designer",
            "Costume Designer"
        };

        private static void RemoveAllPersonCategories(HashSet<string> catSet)
        {
            var toRemove = catSet.Where(c => PersonCategories.Contains(c)).ToList();
            foreach (var c in toRemove)
                catSet.Remove(c);
        }

        static IEnumerable<string> DistinctIgnoreCase(IEnumerable<string> items)
            => items.Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Try to pull an explicit movie title out of phrases like:
        ///   "from the movie Ghost"
        ///   "from a movie called The Shining"
        ///   "in the film called Alien"
        /// </summary>
        private static string? ExtractExplicitMovieTitle(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return null;

            var m = Regex.Match(
                prompt,
                @"(?:from|in)\s+(?:a\s+|the\s+)?movie\s+(?:called\s+)?(?<title>[A-Za-z0-9][A-Za-z0-9\s:'\-]*)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (!m.Success)
            {
                m = Regex.Match(
                    prompt,
                    @"(?:from|in)\s+(?:a\s+|the\s+)?film\s+(?:called\s+)?(?<title>[A-Za-z0-9][A-Za-z0-9\s:'\-]*)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            if (!m.Success)
                return null;

            var raw = m.Groups["title"].Value.Trim();
            raw = Regex.Replace(raw, @"[.,;:!?]+$", "").Trim();
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }

        private static string ExtractDescription(
            string prompt,
            IEnumerable<KeywordResult> include,
            IEnumerable<KeywordResult> exclude)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return string.Empty;

            var result = prompt;

            var keywords = include
                .Concat(exclude)
                .Select(k => k.Keyword)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(k => k.Length)
                .ToList();

            foreach (var kw in keywords)
            {
                var pattern = $@"\b{Regex.Escape(kw)}\b";
                result = Regex.Replace(result, pattern, string.Empty, RegexOptions.IgnoreCase);
            }

            result = Regex.Replace(
                result,
                @"\b(directed by|directed|starring|staring|starred by|featuring|with|from the movie|from a movie|from the film|from a film|in the movie|in a movie|in the film|in a film|movie called|film called)\b\s*",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            result = Regex.Replace(result, @"\s*,\s*(and|or)\s*", " ", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\s*(and|or)\s*,\s*", " ", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\s{2,}", " ");
            result = Regex.Replace(result, @"\s+([,.;:])", "$1");
            result = Regex.Replace(result, @"\s*(,|and|or)\s*$", string.Empty, RegexOptions.IgnoreCase);

            return result.Trim().Trim(',', ';', ':');
        }

        // ---- CUE REGEXES ----

        private static readonly Regex DirectorCueRx = new(
            @"\b(directed\s+by|a\s+film\s+by|a\s+movie\s+by|helmed\s+by|direction\s+by|from\s+(?:the\s+)?director|by\s+(?:the\s+)?director|by\s+(?:the\s+)?filmmaker)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex ActingCueRx = new(
            @"\b(?:star(?:ring|red)?|co-?star(?:ring|red)?|headlin(?:ed|es|ing)\s+by|led\s+by|fronted\s+by|with|including|featuring|cast(?:\s+includes)?|acted\s+by|acting\s+by|performance\s+by|appearing\s+in|voice(?:d)?\s+by|voice\s+cast\s+includes|cameo\s+by|plays?\s+|as\s+\w+)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex CinematographyCueRx = new(
            @"\b(?:shot\s+by|cinematography\s+by|director\s+of\s+photography|photography\s+by|dp)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex ProductionDesignCueRx = new(
            @"\b(?:production\s+designer|production\s+design(?:er)?\s+by)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex CostumeDesignCueRx = new(
            @"\b(?:costume\s+designer|costume\s+design(?:er)?\s+by)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex TitleCueRx = new(
            @"\b(?:(?:from|in)\s+(?:a\s+|the\s+)?(?:movie|film|tv\s+show|series|episode|music\s+video|commercial|ad)(?:\s+called)?|(?:movie|film|tv\s+show|series|episode|music\s+video|commercial|ad)\s+called)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private record CueIndex(
            string Lower,
            string[] Tokens,
            Dictionary<int, int> CharToToken,
            List<int> DirectorTokenPositions,
            List<int> ActingTokenPositions,
            List<int> CinematographyTokenPositions,
            List<int> ProductionDesignTokenPositions,
            List<int> CostumeDesignTokenPositions,
            List<int> TitleTokenPositions
        );

        private static CueIndex BuildCueIndex(string q)
        {
            var lower = (q ?? string.Empty).ToLowerInvariant();

            var tokens = SplitWithOffsets(lower, out var tokenStarts);
            var charToToken = new Dictionary<int, int>();
            for (int ti = 0; ti < tokenStarts.Count; ti++)
            {
                int start = tokenStarts[ti];
                for (int i = start; i < start + tokens[ti].Length; i++)
                    charToToken[i] = ti;
            }

            var directorCharPos = FindAllMatches(DirectorCueRx, lower);
            var actingCharPos = FindAllMatches(ActingCueRx, lower);
            var cineCharPos = FindAllMatches(CinematographyCueRx, lower);
            var prodCharPos = FindAllMatches(ProductionDesignCueRx, lower);
            var costumeCharPos = FindAllMatches(CostumeDesignCueRx, lower);
            var titleCharPos = FindAllMatches(TitleCueRx, lower);

            var directorTokPos = directorCharPos.Select(p => CharIndexToTokenIndex(p, charToToken)).Where(i => i >= 0).ToList();
            var actingTokPos = actingCharPos.Select(p => CharIndexToTokenIndex(p, charToToken)).Where(i => i >= 0).ToList();
            var cineTokPos = cineCharPos.Select(p => CharIndexToTokenIndex(p, charToToken)).Where(i => i >= 0).ToList();
            var prodTokPos = prodCharPos.Select(p => CharIndexToTokenIndex(p, charToToken)).Where(i => i >= 0).ToList();
            var costumeTokPos = costumeCharPos.Select(p => CharIndexToTokenIndex(p, charToToken)).Where(i => i >= 0).ToList();
            var titleTokPos = titleCharPos.Select(p => CharIndexToTokenIndex(p, charToToken)).Where(i => i >= 0).ToList();

            return new CueIndex(lower, tokens, charToToken, directorTokPos, actingTokPos, cineTokPos, prodTokPos, costumeTokPos, titleTokPos);
        }

        private static List<int> FindAllMatches(Regex rx, string s)
        {
            var list = new List<int>();
            var m = rx.Matches(s);
            foreach (Match mm in m)
                if (mm.Success) list.Add(mm.Index);
            return list;
        }

        private static int CharIndexToTokenIndex(int charIdx, Dictionary<int, int> charToToken)
        {
            for (int i = charIdx; i >= 0; i--)
                if (charToToken.TryGetValue(i, out var tok)) return tok;
            return -1;
        }

        private static string[] SplitWithOffsets(string s, out List<int> starts)
        {
            var tokens = new List<string>();
            starts = new List<int>();
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i >= s.Length) break;

                int start = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
                tokens.Add(s[start..i]);
                starts.Add(start);
            }
            return tokens.ToArray();
        }

        // ✅ Finds all whole-word occurrences (fixes "kid ... directed by kid")
        private static List<int> FindAllWholeWordPositions(string lowerPrompt, string keyword)
        {
            if (string.IsNullOrWhiteSpace(lowerPrompt) || string.IsNullOrWhiteSpace(keyword))
                return new List<int>();

            var rx = new Regex(@"\b" + Regex.Escape(keyword.ToLowerInvariant()) + @"\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            var list = new List<int>();
            foreach (Match m in rx.Matches(lowerPrompt))
                if (m.Success) list.Add(m.Index);

            return list;
        }

        private static (bool director, bool acting, bool cine, bool prod, bool costume, bool title)
            GetLocalIntentForKeyword(string prompt, CueIndex idx, string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                return (false, false, false, false, false, false);

            var occurrences = FindAllWholeWordPositions(idx.Lower, keyword.Trim());
            if (occurrences.Count == 0)
                return (false, false, false, false, false, false);

            const int WINDOW = 8;

            bool directorClose = false, actingClose = false, cineClose = false, prodClose = false, costumeClose = false, titleClose = false;

            foreach (var charPos in occurrences)
            {
                int kwTok = CharIndexToTokenIndex(charPos, idx.CharToToken);
                if (kwTok < 0) continue;

                int? dDist = NearestDistance(kwTok, idx.DirectorTokenPositions);
                int? aDist = NearestDistance(kwTok, idx.ActingTokenPositions);
                int? cDist = NearestDistance(kwTok, idx.CinematographyTokenPositions);
                int? pDist = NearestDistance(kwTok, idx.ProductionDesignTokenPositions);
                int? kDist = NearestDistance(kwTok, idx.CostumeDesignTokenPositions);

                int? tDistForward = NearestForwardDistance(kwTok, idx.TitleTokenPositions);

                directorClose |= dDist.HasValue && dDist.Value <= WINDOW;
                actingClose |= aDist.HasValue && aDist.Value <= WINDOW;
                cineClose |= cDist.HasValue && cDist.Value <= WINDOW;
                prodClose |= pDist.HasValue && pDist.Value <= WINDOW;
                costumeClose |= kDist.HasValue && kDist.Value <= WINDOW;
                titleClose |= tDistForward.HasValue && tDistForward.Value <= WINDOW;
            }

            return (directorClose, actingClose, cineClose, prodClose, costumeClose, titleClose);
        }

        private static int? NearestForwardDistance(int keywordTok, List<int> cuePositions)
        {
            if (cuePositions == null || cuePositions.Count == 0) return null;

            int best = int.MaxValue;
            foreach (var cueTok in cuePositions)
            {
                int dist = keywordTok - cueTok;
                if (dist < 0) continue;
                if (dist < best) best = dist;
            }
            return best == int.MaxValue ? null : best;
        }

        private static int? NearestDistance(int center, List<int> positions)
        {
            if (positions == null || positions.Count == 0) return null;
            int best = int.MaxValue;
            foreach (var p in positions)
            {
                int dist = Math.Abs(p - center);
                if (dist < best) best = dist;
            }
            return best;
        }

        internal static void ApplyTitleFilter(HashSet<string> catSet, bool keepTitle)
        {
            if (!keepTitle)
            {
                var toRemove = catSet
                    .Where(c => c.Equals("Title", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var c in toRemove)
                    catSet.Remove(c);

                return;
            }

            var ambiguousNonTitle = catSet
                .Where(c =>
                    c.Equals("mv_artist", StringComparison.OrdinalIgnoreCase) ||
                    c.Equals("Music Video Artist", StringComparison.OrdinalIgnoreCase) ||
                    c.Equals("comm_brand", StringComparison.OrdinalIgnoreCase) ||
                    c.Equals("Commercial Brand", StringComparison.OrdinalIgnoreCase)
                )
                .ToList();

            foreach (var c in ambiguousNonTitle)
                catSet.Remove(c);
        }

        internal static string MapSourceLabelToCategory(string src)
        {
            if (string.IsNullOrWhiteSpace(src)) return src;

            var s = src.Trim();
            if (s.StartsWith("movie:", StringComparison.OrdinalIgnoreCase)) s = s[6..];
            if (s.StartsWith("image:", StringComparison.OrdinalIgnoreCase)) s = s[6..];

            if (s.Equals("actors", StringComparison.OrdinalIgnoreCase)) return "Actors";
            if (s.Equals("actor", StringComparison.OrdinalIgnoreCase)) return "Actors";
            if (s.Equals("cast", StringComparison.OrdinalIgnoreCase)) return "Cast";
            if (s.Equals("director", StringComparison.OrdinalIgnoreCase)) return "Director";
            if (s.Equals("cinematographer", StringComparison.OrdinalIgnoreCase)) return "Cinematographer";
            if (s.Equals("production_designer", StringComparison.OrdinalIgnoreCase)) return "Production Designer";
            if (s.Equals("costume_designer", StringComparison.OrdinalIgnoreCase)) return "Costume Designer";

            return s;
        }

        internal static void ApplyIntentFilter(
    HashSet<string> catSet,
    bool director,
    bool acting,
    bool cinematographer,
    bool productionDesigner,
    bool costumeDesigner)
        {
            if (!director && !acting && !cinematographer && !productionDesigner && !costumeDesigner)
                return;

            // Identify "person/crew" buckets present on this keyword
            bool IsActorBucket(string c) =>
                c.Equals("Actors", StringComparison.OrdinalIgnoreCase) ||
                c.Equals("Actor", StringComparison.OrdinalIgnoreCase) ||
                c.Equals("Cast", StringComparison.OrdinalIgnoreCase) ||
                c.IndexOf("actor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                c.IndexOf("cast", StringComparison.OrdinalIgnoreCase) >= 0;

            bool IsDirectorBucket(string c) =>
                c.Equals("Director", StringComparison.OrdinalIgnoreCase);

            bool IsCineBucket(string c) =>
                c.Equals("Cinematographer", StringComparison.OrdinalIgnoreCase);

            bool IsProdBucket(string c) =>
                c.Equals("Production Designer", StringComparison.OrdinalIgnoreCase);

            bool IsCostumeBucket(string c) =>
                c.Equals("Costume Designer", StringComparison.OrdinalIgnoreCase);

            bool IsPersonOrCrew(string c) =>
                IsActorBucket(c) || IsDirectorBucket(c) || IsCineBucket(c) || IsProdBucket(c) || IsCostumeBucket(c);

            var personCrewNow = catSet.Where(IsPersonOrCrew).ToList();
            if (personCrewNow.Count == 0) return;

            // Decide what to keep
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // If director intent is present, you generally want ONLY Director for that keyword,
            // unless you also explicitly asked for other roles in the same local window.
            if (director)
            {
                if (personCrewNow.Any(IsDirectorBucket))
                    keep.Add("Director");

                // If the local phrase ALSO indicates other roles, keep them too.
                // Example: "directed by and shot by Quentin Tarantino" (rare but possible)
                if (cinematographer && personCrewNow.Any(IsCineBucket)) keep.Add("Cinematographer");
                if (productionDesigner && personCrewNow.Any(IsProdBucket)) keep.Add("Production Designer");
                if (costumeDesigner && personCrewNow.Any(IsCostumeBucket)) keep.Add("Costume Designer");

                if (acting)
                {
                    foreach (var c in personCrewNow.Where(IsActorBucket))
                        keep.Add(c);
                }
            }
            else
            {
                // No director intent, so keep any explicitly requested roles that exist
                if (acting)
                {
                    foreach (var c in personCrewNow.Where(IsActorBucket))
                        keep.Add(c);
                }

                if (cinematographer && personCrewNow.Any(IsCineBucket)) keep.Add("Cinematographer");
                if (productionDesigner && personCrewNow.Any(IsProdBucket)) keep.Add("Production Designer");
                if (costumeDesigner && personCrewNow.Any(IsCostumeBucket)) keep.Add("Costume Designer");
            }

            // If we asked for roles but none exist, allow fallthrough (don't wipe categories).
            // But if we kept something, replace person/crew buckets with keep set.
            if (keep.Count > 0)
            {
                var nonPersonCrew = catSet.Except(personCrewNow, StringComparer.OrdinalIgnoreCase).ToList();
                catSet.Clear();

                foreach (var c in keep) catSet.Add(c);
                foreach (var c in nonPersonCrew) catSet.Add(c);
            }
        }


        internal static void ApplyCrewIntentFilter(HashSet<string> catSet, bool cine, bool prod, bool costume)
        {
            if (!cine && !prod && !costume) return;

            var crew = catSet.Where(c =>
                c.Equals("Cinematographer", StringComparison.OrdinalIgnoreCase) ||
                c.Equals("Production Designer", StringComparison.OrdinalIgnoreCase) ||
                c.Equals("Costume Designer", StringComparison.OrdinalIgnoreCase)
            ).ToList();

            if (crew.Count == 0) return;

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (cine) keep.Add("Cinematographer");
            if (prod) keep.Add("Production Designer");
            if (costume) keep.Add("Costume Designer");

            var nonCrew = catSet.Except(crew, StringComparer.OrdinalIgnoreCase).ToList();

            catSet.Clear();
            foreach (var c in keep) catSet.Add(c);
            foreach (var c in nonCrew) catSet.Add(c);
        }

        [HttpGet("keywords/multi-category.csv")]
        public IActionResult GenerateKeywordsMultiCategoryCsv([FromQuery] int minCount = 2, [FromQuery] bool onlyKeywords = false)
        {
            try
            {
                var env = HttpContext?.RequestServices?.GetService<IWebHostEnvironment>();
                var hasWebRoot = !string.IsNullOrWhiteSpace(env?.WebRootPath);

                var baseDir = hasWebRoot
                    ? Path.Combine(env!.WebRootPath!, "exports")
                    : Path.Combine(Path.GetTempPath(), "exports");

                Directory.CreateDirectory(baseDir);

                var fileName = $"keywords-multi-category-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
                var fullPath = Path.Combine(baseDir, fileName);

                var rows = new List<(string Keyword, List<string> Categories, int Count)>();
                var kwToSources = _keywordCache.GetKeywordSources();

                foreach (var kvp in kwToSources)
                {
                    var keyword = kvp.Key;
                    if (string.IsNullOrWhiteSpace(keyword)) continue;

                    var catSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    bool hasKeywordsCategory = false;

                    foreach (var src in kvp.Value ?? Array.Empty<string>())
                    {
                        var cat = MapSourceToCategory(src);
                        if (!string.IsNullOrWhiteSpace(cat))
                        {
                            catSet.Add(cat);
                            if (cat.Equals("Keywords", StringComparison.OrdinalIgnoreCase))
                                hasKeywordsCategory = true;
                        }

                        if (src.Equals("keywords", StringComparison.OrdinalIgnoreCase))
                            hasKeywordsCategory = true;
                    }

                    if (catSet.Count < minCount) continue;
                    if (onlyKeywords && !hasKeywordsCategory) continue;

                    rows.Add((keyword, catSet.OrderBy(c => c).ToList(), catSet.Count));
                }

                var ordered = rows
                    .OrderByDescending(r => r.Count)
                    .ThenBy(r => r.Keyword, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                using (var fs = System.IO.File.Create(fullPath))
                using (var writer = new StreamWriter(fs, new UTF8Encoding(false), 4096))
                {
                    writer.WriteLine("keyword,categories,count");
                    foreach (var r in ordered)
                    {
                        var categoriesJoined = string.Join("; ", r.Categories);
                        writer.WriteLine($"{Csv(r.Keyword)},{Csv(categoriesJoined)},{r.Count}");
                    }
                    writer.Flush();
                }

                var relativeUrl = hasWebRoot ? $"/exports/{fileName}" : null;
                var sizeBytes = new System.IO.FileInfo(fullPath).Length;

                return Ok(new
                {
                    fileName,
                    path = fullPath,
                    url = relativeUrl,
                    sizeBytes,
                    filters = new { minCount, onlyKeywords },
                    total = ordered.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating multi-category CSV");
#if DEBUG
                return StatusCode(500, new { message = ex.Message, stack = ex.StackTrace });
#else
                return StatusCode(500, "An unexpected error occurred.");
#endif
            }

            static string Csv(string? s)
            {
                if (string.IsNullOrEmpty(s)) return "\"\"";
                var needsQuotes = s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
                var escaped = s.Replace("\"", "\"\"");
                return needsQuotes ? $"\"{escaped}\"" : escaped;
            }
        }

        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh()
        {
            await _keywordCache.RefreshAsync();
            return Ok("Keyword cache refreshed.");
        }

        private static string MapSourceToCategory(string source)
        {
            var s = (source ?? "").ToLowerInvariant();

            return s switch
            {
                "shot_type" => "Shot Type",
                "lighting_type" => "Lighting Type",
                "lighting" => "Lighting",
                "time_of_day" => "Time of Day",
                "vfx_backing" => "VFX Backing",
                "color" => "Color",
                "lens size" => "Lens Size",
                "composition" => "Composition",
                "actors" => "Actors",
                "int_ext" => "Interior/Exterior",
                "aspect_ratio" => "Aspect Ratio",
                _ when s.StartsWith("movie:") => MapSourceToCategory(s.Substring("movie:".Length)),
                _ when s.StartsWith("image:") => MapSourceToCategory(s.Substring("image:".Length)),
                "title" => "Title",
                "media_type" => "Media Type",
                "cast" => "Cast",
                "director" => "Director",
                "cinematographer" => "Cinematographer",
                "production_designer" => "Production Designer",
                "costume_designer" => "Costume Designer",
                "mv_artist" => "Music Video Artist",
                "comm_brand" => "Commercial Brand",
                _ => source
            };
        }

        public sealed class KeywordInfoDto
        {
            public string Keyword { get; set; } = "";
            public List<string> Categories { get; set; } = new();
            public List<string> Sources { get; set; } = new();
        }

        public sealed class SearchRequest
        {
            public string Phrase { get; set; } = "";
        }

        public sealed class SearchHitDto
        {
            public string Keyword { get; set; } = "";
            public List<string> Sources { get; set; } = new();
        }
    }
}
