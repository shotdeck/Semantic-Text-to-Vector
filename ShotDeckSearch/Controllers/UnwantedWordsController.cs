using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using System.Data;
using System.Globalization;

namespace ShotDeckSearch.Controllers
{
    [ApiController]
    [Route("api/admin/unwanted-words")]
    public sealed class UnwantedWordsAdminController : ControllerBase
    {
        private readonly NpgsqlConnection _connection;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<UnwantedWordsAdminController> _logger;

        public UnwantedWordsAdminController(
            NpgsqlConnection connection,
            IServiceScopeFactory scopeFactory,
            ILogger<UnwantedWordsAdminController> logger)
        {
            _connection = connection;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        /// <summary>
        /// POST /api/admin/unwanted-words/import-csv
        /// multipart/form-data:
        ///   - file: CSV with columns:
        ///       WORD (required) - the unwanted phrase
        ///       SUPER BLACKLIST (optional) - TRUE/FALSE for substring matching
        ///       NOTES (ignored)
        ///   - dryRun: optional bool
        ///
        /// Behavior:
        ///   - If dryRun=true: parses + counts only (rolls back)
        ///   - If dryRun=false: clears table first, then imports everything
        /// </summary>
        [HttpPost("import-csv")]
        [Consumes("multipart/form-data")]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<ImportUnwantedWordsResult>> ImportCsv(
            [FromForm] ImportCsvRequest req,
            CancellationToken ct)
        {
            if (req.File is null || req.File.Length == 0)
                return BadRequest("CSV file is required.");

            var dryRun = req.DryRun ?? false;

            var mustClose = false;
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(ct);
                mustClose = true;
            }

            await using var tx = await _connection.BeginTransactionAsync(ct);

            try
            {
                const string deleteSql = @"DELETE FROM frl.frl_keywords_unwanted_words;";

                if (!dryRun)
                {
                    await using (var c1 = new NpgsqlCommand(deleteSql, _connection, tx))
                        await c1.ExecuteNonQueryAsync(ct);
                }

                const string insertSql = @"
INSERT INTO frl.frl_keywords_unwanted_words (phrase, is_super_blacklist)
VALUES (@phrase, @is_super_blacklist)
ON CONFLICT (phrase) DO UPDATE SET is_super_blacklist = EXCLUDED.is_super_blacklist
RETURNING id;";

                await using var insertCmd = new NpgsqlCommand(insertSql, _connection, tx);
                insertCmd.Parameters.Add("@phrase", NpgsqlDbType.Text);
                insertCmd.Parameters.Add("@is_super_blacklist", NpgsqlDbType.Boolean);

                var result = new ImportUnwantedWordsResult();

                using var reader = new StreamReader(req.File.OpenReadStream());

                var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    TrimOptions = TrimOptions.Trim,
                    IgnoreBlankLines = true,
                    BadDataFound = null,
                    MissingFieldFound = null,
                    HeaderValidated = null,
                    PrepareHeaderForMatch = args => (args.Header ?? "").Trim()
                };

                using var csv = new CsvReader(reader, csvConfig);

                // Skip any leading notes/comments until we find the header row
                string[]? headers = null;
                while (await csv.ReadAsync())
                {
                    csv.ReadHeader();
                    headers = csv.HeaderRecord ?? Array.Empty<string>();
                    
                    // Check if this row contains the WORD header
                    var wordHeader = FindHeader(headers, "WORD");
                    if (!string.IsNullOrWhiteSpace(wordHeader))
                        break;
                }

                if (headers == null || headers.Length == 0)
                    return BadRequest("CSV appears to be empty.");

                var wordHeaderName = FindHeader(headers, "WORD");
                if (string.IsNullOrWhiteSpace(wordHeaderName))
                    return BadRequest("CSV must contain a 'WORD' column.");

                // Find SUPER BLACKLIST column (flexible naming)
                var superBlacklistHeader = headers
                    .FirstOrDefault(h => !string.IsNullOrWhiteSpace(h) &&
                        (h.Trim().StartsWith("SUPER BLACKLIST", StringComparison.OrdinalIgnoreCase) ||
                         h.Trim().Equals("SUPER_BLACKLIST", StringComparison.OrdinalIgnoreCase) ||
                         h.Trim().Equals("IS_SUPER_BLACKLIST", StringComparison.OrdinalIgnoreCase)));

                while (await csv.ReadAsync())
                {
                    result.RowsRead++;

                    var phrase = (SafeGet(csv, wordHeaderName) ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(phrase))
                    {
                        result.RowsSkipped++;
                        continue;
                    }

                    // Parse super blacklist flag
                    var isSuperBlacklist = false;
                    if (!string.IsNullOrWhiteSpace(superBlacklistHeader))
                    {
                        var superBlacklistValue = (SafeGet(csv, superBlacklistHeader) ?? "").Trim();
                        isSuperBlacklist = string.Equals(superBlacklistValue, "TRUE", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(superBlacklistValue, "1", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(superBlacklistValue, "YES", StringComparison.OrdinalIgnoreCase);
                    }

                    result.PhrasesSeen++;
                    if (isSuperBlacklist)
                        result.SuperBlacklistCount++;

                    if (dryRun)
                    {
                        result.PhrasesInserted++;
                        continue;
                    }

                    insertCmd.Parameters["@phrase"].Value = phrase;
                    insertCmd.Parameters["@is_super_blacklist"].Value = isSuperBlacklist;

                    var scalar = await insertCmd.ExecuteScalarAsync(ct);

                    if (scalar is null)
                    {
                        result.Errors.Add(new ImportRowError
                        {
                            RowNumber = result.RowsRead,
                            Phrase = phrase,
                            Message = "Insert returned null id."
                        });
                        continue;
                    }

                    result.PhrasesInserted++;
                }

                if (dryRun)
                {
                    await tx.RollbackAsync(ct);
                    result.DryRun = true;
                    return Ok(result);
                }

                await tx.CommitAsync(ct);
                return Ok(result);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                _logger.LogError(ex, "Unwanted words CSV import failed.");
                throw;
            }
            finally
            {
                if (mustClose) await _connection.CloseAsync();
            }
        }

        #region Helpers

        private static string? FindHeader(string[] headers, string wanted)
            => headers.FirstOrDefault(h =>
                string.Equals((h ?? "").Trim(), wanted, StringComparison.OrdinalIgnoreCase));

        private static string? SafeGet(CsvReader csv, string header)
        {
            try { return csv.GetField(header); }
            catch { return null; }
        }

        #endregion

        #region DTOs

        public sealed class ImportCsvRequest
        {
            public IFormFile? File { get; set; }
            public bool? DryRun { get; set; }
        }

        public sealed class ImportUnwantedWordsResult
        {
            public bool DryRun { get; set; }

            public int RowsRead { get; set; }
            public int RowsSkipped { get; set; }

            public int PhrasesSeen { get; set; }
            public int PhrasesInserted { get; set; }
            public int SuperBlacklistCount { get; set; }

            public List<ImportRowError> Errors { get; set; } = new();
        }

        public sealed class ImportRowError
        {
            public int RowNumber { get; set; }
            public string? Phrase { get; set; }
            public string Message { get; set; } = default!;
        }

        #endregion
    }
}
