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
    [Route("api/admin/synonyms")]
    public sealed class SynonymsAdminController : ControllerBase
    {
        private readonly NpgsqlConnection _connection;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SynonymsAdminController> _logger;

        public SynonymsAdminController(
            NpgsqlConnection connection,
            IServiceScopeFactory scopeFactory,
            ILogger<SynonymsAdminController> logger)
        {
            _connection = connection;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        /// <summary>
        /// POST /api/admin/synonyms/import-csv
        /// multipart/form-data:
        ///   - file: CSV with columns:
        ///       NOTES (ignored)
        ///       COLIN ADDED TO SITE? (ignored)
        ///       TYPE (ignored)
        ///       MASTER TERM (required)
        ///       ALT TERM 1..ALT TERM N (optional, flexible)
        ///   - dryRun: optional bool
        ///
        /// Behavior:
        ///   - If dryRun=true: parses + counts only (rolls back)
        ///   - If dryRun=false: clears BOTH tables first, then imports everything
        /// </summary>
        [HttpPost("import-csv")]
        [Consumes("multipart/form-data")]
        [ProducesResponseType(typeof(ImportSynonymsResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<ImportSynonymsResult>> ImportCsv(
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
                // Clear tables (child first)
                const string deleteSynSql = @"DELETE FROM frl.frl_keywords_synonyms;";
                const string deleteMasterSql = @"DELETE FROM frl.frl_keywords_synonyms_master;";

                if (!dryRun)
                {
                    await using (var c1 = new NpgsqlCommand(deleteSynSql, _connection, tx))
                        await c1.ExecuteNonQueryAsync(ct);

                    await using (var c2 = new NpgsqlCommand(deleteMasterSql, _connection, tx))
                        await c2.ExecuteNonQueryAsync(ct);
                }

                const string upsertMasterSql = @"
INSERT INTO frl.frl_keywords_synonyms_master (master_term)
VALUES (@master_term)
ON CONFLICT (master_term)
DO UPDATE SET master_term = EXCLUDED.master_term
RETURNING id;";

                const string insertSynSql = @"
INSERT INTO frl.frl_keywords_synonyms (master_id, synonym_term, is_included)
VALUES (@master_id, @synonym_term, TRUE)
ON CONFLICT (master_id, synonym_term) DO NOTHING;";

                await using var upsertMasterCmd = new NpgsqlCommand(upsertMasterSql, _connection, tx);
                upsertMasterCmd.Parameters.Add("@master_term", NpgsqlDbType.Text);

                await using var insertSynCmd = new NpgsqlCommand(insertSynSql, _connection, tx);
                insertSynCmd.Parameters.Add("@master_id", NpgsqlDbType.Integer);
                insertSynCmd.Parameters.Add("@synonym_term", NpgsqlDbType.Text);

                var result = new ImportSynonymsResult();

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

                if (!await csv.ReadAsync())
                    return BadRequest("CSV appears to be empty.");

                csv.ReadHeader();
                var headers = csv.HeaderRecord ?? Array.Empty<string>();

                var masterHeader = FindHeader(headers, "MASTER TERM");
                if (string.IsNullOrWhiteSpace(masterHeader))
                    return BadRequest("CSV must contain a 'MASTER TERM' column.");

                // Flexible ALT TERM columns (ALT TERM 1..N, ALT TERM 10+ supported)
                var altHeaders = headers
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Select(h => h.Trim())
                    .Where(h => h.StartsWith("ALT TERM", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                while (await csv.ReadAsync())
                {
                    result.RowsRead++;

                    var master = (SafeGet(csv, masterHeader) ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(master))
                    {
                        result.RowsSkipped++;
                        continue;
                    }

                    var alts = new List<string>();
                    foreach (var h in altHeaders)
                    {
                        var v = (SafeGet(csv, h) ?? "").Trim();
                        if (string.IsNullOrWhiteSpace(v)) continue;
                        if (string.Equals(v, master, StringComparison.OrdinalIgnoreCase)) continue;
                        alts.Add(v);
                    }

                    alts = alts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                    result.MastersSeen++;
                    if (alts.Count == 0)
                        result.RowsWithNoAlts++;

                    int masterId;

                    if (dryRun)
                    {
                        result.MastersUpserted++;
                        masterId = -1;
                    }
                    else
                    {
                        upsertMasterCmd.Parameters["@master_term"].Value = master;
                        var scalar = await upsertMasterCmd.ExecuteScalarAsync(ct);

                        if (scalar is null)
                        {
                            result.Errors.Add(new ImportRowError
                            {
                                RowNumber = result.RowsRead,
                                MasterTerm = master,
                                Message = "Upsert master returned null id."
                            });
                            continue;
                        }

                        masterId = Convert.ToInt32(scalar);
                        result.MastersUpserted++;
                    }

                    foreach (var alt in alts)
                    {
                        result.SynonymsSeen++;

                        if (dryRun)
                        {
                            result.SynonymsInserted++;
                            continue;
                        }

                        insertSynCmd.Parameters["@master_id"].Value = masterId;
                        insertSynCmd.Parameters["@synonym_term"].Value = alt;

                        var rows = await insertSynCmd.ExecuteNonQueryAsync(ct);
                        if (rows == 1) result.SynonymsInserted++;
                        else result.SynonymsAlreadyExisted++;
                    }
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
                _logger.LogError(ex, "Synonyms CSV import failed.");
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

        public sealed class ImportSynonymsResult
        {
            public bool DryRun { get; set; }

            public int RowsRead { get; set; }
            public int RowsSkipped { get; set; }
            public int RowsWithNoAlts { get; set; }

            public int MastersSeen { get; set; }
            public int MastersUpserted { get; set; }

            public int SynonymsSeen { get; set; }
            public int SynonymsInserted { get; set; }
            public int SynonymsAlreadyExisted { get; set; }

            public List<ImportRowError> Errors { get; set; } = new();
        }

        public sealed class ImportRowError
        {
            public int RowNumber { get; set; }
            public string? MasterTerm { get; set; }
            public string Message { get; set; } = default!;
        }

        #endregion
    }
}
