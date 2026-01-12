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

        #region Master Terms CRUD

        /// <summary>
        /// GET /api/admin/synonyms/masters
        /// Returns all master terms from the database.
        /// </summary>
        [HttpGet("masters")]
        [ProducesResponseType(typeof(List<MasterTermDto>), StatusCodes.Status200OK)]
        public async Task<ActionResult<List<MasterTermDto>>> GetAllMasters(CancellationToken ct)
        {
            var mustClose = false;
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(ct);
                mustClose = true;
            }

            try
            {
                const string sql = @"SELECT id, master_term, is_included FROM frl.frl_keywords_synonyms_master ORDER BY master_term;";
                await using var cmd = new NpgsqlCommand(sql, _connection);
                await using var reader = await cmd.ExecuteReaderAsync(ct);

                var results = new List<MasterTermDto>();
                while (await reader.ReadAsync(ct))
                {
                    results.Add(new MasterTermDto
                    {
                        Id = reader.GetInt32(0),
                        MasterTerm = reader.GetString(1),
                        IsIncluded = !reader.IsDBNull(2) && reader.GetBoolean(2)
                    });
                }

                return Ok(results);
            }
            finally
            {
                if (mustClose) await _connection.CloseAsync();
            }
        }

        /// <summary>
        /// GET /api/admin/synonyms/masters/{id}
        /// Returns a single master term by ID.
        /// </summary>
        [HttpGet("masters/{id:int}")]
        [ProducesResponseType(typeof(MasterTermDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<MasterTermDto>> GetMasterById(int id, CancellationToken ct)
        {
            var mustClose = false;
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(ct);
                mustClose = true;
            }

            try
            {
                const string sql = @"SELECT id, master_term, is_included FROM frl.frl_keywords_synonyms_master WHERE id = @id;";
                await using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("@id", id);
                await using var reader = await cmd.ExecuteReaderAsync(ct);

                if (!await reader.ReadAsync(ct))
                    return NotFound(new { Message = $"Master term with ID {id} not found." });

                return Ok(new MasterTermDto
                {
                    Id = reader.GetInt32(0),
                    MasterTerm = reader.GetString(1),
                    IsIncluded = !reader.IsDBNull(2) && reader.GetBoolean(2)
                });
            }
            finally
            {
                if (mustClose) await _connection.CloseAsync();
            }
        }

        /// <summary>
        /// POST /api/admin/synonyms/masters
        /// Creates a new master term.
        /// </summary>
        [HttpPost("masters")]
        [ProducesResponseType(typeof(MasterTermDto), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<MasterTermDto>> CreateMaster([FromBody] CreateMasterTermRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.MasterTerm))
                return BadRequest(new { Message = "MasterTerm is required." });

            var mustClose = false;
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(ct);
                mustClose = true;
            }

            try
            {
                const string sql = @"
INSERT INTO frl.frl_keywords_synonyms_master (master_term, is_included)
VALUES (@master_term, @is_included)
RETURNING id, master_term, is_included;";

                await using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("@master_term", request.MasterTerm.Trim());
                cmd.Parameters.AddWithValue("@is_included", request.IsIncluded ?? true);

                await using var reader = await cmd.ExecuteReaderAsync(ct);

                if (!await reader.ReadAsync(ct))
                    return BadRequest(new { Message = "Failed to create master term." });

                var result = new MasterTermDto
                {
                    Id = reader.GetInt32(0),
                    MasterTerm = reader.GetString(1),
                    IsIncluded = !reader.IsDBNull(2) && reader.GetBoolean(2)
                };

                return CreatedAtAction(nameof(GetMasterById), new { id = result.Id }, result);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                return Conflict(new { Message = $"A master term '{request.MasterTerm}' already exists." });
            }
            finally
            {
                if (mustClose) await _connection.CloseAsync();
            }
        }

        /// <summary>
        /// PUT /api/admin/synonyms/masters/{id}
        /// Updates an existing master term.
        /// </summary>
        [HttpPut("masters/{id:int}")]
        [ProducesResponseType(typeof(MasterTermDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<MasterTermDto>> UpdateMaster(int id, [FromBody] UpdateMasterTermRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.MasterTerm))
                return BadRequest(new { Message = "MasterTerm is required." });

            var mustClose = false;
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(ct);
                mustClose = true;
            }

            try
            {
                const string sql = @"
UPDATE frl.frl_keywords_synonyms_master
SET master_term = @master_term, is_included = @is_included
WHERE id = @id
RETURNING id, master_term, is_included;";

                await using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@master_term", request.MasterTerm.Trim());
                cmd.Parameters.AddWithValue("@is_included", request.IsIncluded ?? true);

                await using var reader = await cmd.ExecuteReaderAsync(ct);

                if (!await reader.ReadAsync(ct))
                    return NotFound(new { Message = $"Master term with ID {id} not found." });

                return Ok(new MasterTermDto
                {
                    Id = reader.GetInt32(0),
                    MasterTerm = reader.GetString(1),
                    IsIncluded = !reader.IsDBNull(2) && reader.GetBoolean(2)
                });
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                return Conflict(new { Message = $"A master term '{request.MasterTerm}' already exists." });
            }
            finally
            {
                if (mustClose) await _connection.CloseAsync();
            }
        }

        /// <summary>
        /// DELETE /api/admin/synonyms/masters/{id}
        /// Deletes a master term by ID (cascades to synonyms).
        /// </summary>
        [HttpDelete("masters/{id:int}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> DeleteMaster(int id, CancellationToken ct)
        {
            var mustClose = false;
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(ct);
                mustClose = true;
            }

            try
            {
                const string sql = @"DELETE FROM frl.frl_keywords_synonyms_master WHERE id = @id;";
                await using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("@id", id);

                var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);

                if (rowsAffected == 0)
                    return NotFound(new { Message = $"Master term with ID {id} not found." });

                return NoContent();
            }
            finally
            {
                if (mustClose) await _connection.CloseAsync();
            }
        }

        #endregion

        #region Synonym Terms CRUD

        /// <summary>
        /// GET /api/admin/synonyms/masters/{masterId}/synonyms
        /// Returns all synonyms for a master term.
        /// </summary>
        [HttpGet("masters/{masterId:int}/synonyms")]
        [ProducesResponseType(typeof(List<SynonymDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<List<SynonymDto>>> GetSynonymsByMaster(int masterId, CancellationToken ct)
        {
            var mustClose = false;
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(ct);
                mustClose = true;
            }

            try
            {
                const string checkMasterSql = @"SELECT 1 FROM frl.frl_keywords_synonyms_master WHERE id = @master_id;";
                await using (var checkCmd = new NpgsqlCommand(checkMasterSql, _connection))
                {
                    checkCmd.Parameters.AddWithValue("@master_id", masterId);
                    var exists = await checkCmd.ExecuteScalarAsync(ct);
                    if (exists is null)
                        return NotFound(new { Message = $"Master term with ID {masterId} not found." });
                }

                const string sql = @"SELECT id, master_id, synonym_term, is_included FROM frl.frl_keywords_synonyms WHERE master_id = @master_id ORDER BY synonym_term;";
                await using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("@master_id", masterId);
                await using var reader = await cmd.ExecuteReaderAsync(ct);

                var results = new List<SynonymDto>();
                while (await reader.ReadAsync(ct))
                {
                    results.Add(new SynonymDto
                    {
                        Id = reader.GetInt32(0),
                        MasterId = reader.GetInt32(1),
                        SynonymTerm = reader.GetString(2),
                        IsIncluded = !reader.IsDBNull(3) && reader.GetBoolean(3)
                    });
                }

                return Ok(results);
            }
            finally
            {
                if (mustClose) await _connection.CloseAsync();
            }
        }

        /// <summary>
        /// GET /api/admin/synonyms/synonyms/{id}
        /// Returns a single synonym by ID.
        /// </summary>
        [HttpGet("synonyms/{id:int}")]
        [ProducesResponseType(typeof(SynonymDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<SynonymDto>> GetSynonymById(int id, CancellationToken ct)
        {
            var mustClose = false;
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(ct);
                mustClose = true;
            }

            try
            {
                const string sql = @"SELECT id, master_id, synonym_term, is_included FROM frl.frl_keywords_synonyms WHERE id = @id;";
                await using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("@id", id);
                await using var reader = await cmd.ExecuteReaderAsync(ct);

                if (!await reader.ReadAsync(ct))
                    return NotFound(new { Message = $"Synonym with ID {id} not found." });

                return Ok(new SynonymDto
                {
                    Id = reader.GetInt32(0),
                    MasterId = reader.GetInt32(1),
                    SynonymTerm = reader.GetString(2),
                    IsIncluded = !reader.IsDBNull(3) && reader.GetBoolean(3)
                });
            }
            finally
            {
                if (mustClose) await _connection.CloseAsync();
            }
        }

        /// <summary>
        /// POST /api/admin/synonyms/masters/{masterId}/synonyms
        /// Creates a new synonym for a master term.
        /// </summary>
        [HttpPost("masters/{masterId:int}/synonyms")]
        [ProducesResponseType(typeof(SynonymDto), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<SynonymDto>> CreateSynonym(int masterId, [FromBody] CreateSynonymRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.SynonymTerm))
                return BadRequest(new { Message = "SynonymTerm is required." });

            var mustClose = false;
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(ct);
                mustClose = true;
            }

            try
            {
                const string checkMasterSql = @"SELECT 1 FROM frl.frl_keywords_synonyms_master WHERE id = @master_id;";
                await using (var checkCmd = new NpgsqlCommand(checkMasterSql, _connection))
                {
                    checkCmd.Parameters.AddWithValue("@master_id", masterId);
                    var exists = await checkCmd.ExecuteScalarAsync(ct);
                    if (exists is null)
                        return NotFound(new { Message = $"Master term with ID {masterId} not found." });
                }

                const string sql = @"
INSERT INTO frl.frl_keywords_synonyms (master_id, synonym_term, is_included)
VALUES (@master_id, @synonym_term, @is_included)
RETURNING id, master_id, synonym_term, is_included;";

                await using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("@master_id", masterId);
                cmd.Parameters.AddWithValue("@synonym_term", request.SynonymTerm.Trim());
                cmd.Parameters.AddWithValue("@is_included", request.IsIncluded ?? true);

                await using var reader = await cmd.ExecuteReaderAsync(ct);

                if (!await reader.ReadAsync(ct))
                    return BadRequest(new { Message = "Failed to create synonym." });

                var result = new SynonymDto
                {
                    Id = reader.GetInt32(0),
                    MasterId = reader.GetInt32(1),
                    SynonymTerm = reader.GetString(2),
                    IsIncluded = !reader.IsDBNull(3) && reader.GetBoolean(3)
                };

                return CreatedAtAction(nameof(GetSynonymById), new { id = result.Id }, result);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                return Conflict(new { Message = $"A synonym '{request.SynonymTerm}' already exists for this master term." });
            }
            finally
            {
                if (mustClose) await _connection.CloseAsync();
            }
        }

        /// <summary>
        /// PUT /api/admin/synonyms/synonyms/{id}
        /// Updates an existing synonym.
        /// </summary>
        [HttpPut("synonyms/{id:int}")]
        [ProducesResponseType(typeof(SynonymDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<SynonymDto>> UpdateSynonym(int id, [FromBody] UpdateSynonymRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.SynonymTerm))
                return BadRequest(new { Message = "SynonymTerm is required." });

            var mustClose = false;
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(ct);
                mustClose = true;
            }

            try
            {
                const string sql = @"
UPDATE frl.frl_keywords_synonyms
SET synonym_term = @synonym_term, is_included = @is_included
WHERE id = @id
RETURNING id, master_id, synonym_term, is_included;";

                await using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@synonym_term", request.SynonymTerm.Trim());
                cmd.Parameters.AddWithValue("@is_included", request.IsIncluded ?? true);

                await using var reader = await cmd.ExecuteReaderAsync(ct);

                if (!await reader.ReadAsync(ct))
                    return NotFound(new { Message = $"Synonym with ID {id} not found." });

                return Ok(new SynonymDto
                {
                    Id = reader.GetInt32(0),
                    MasterId = reader.GetInt32(1),
                    SynonymTerm = reader.GetString(2),
                    IsIncluded = !reader.IsDBNull(3) && reader.GetBoolean(3)
                });
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                return Conflict(new { Message = $"A synonym '{request.SynonymTerm}' already exists for this master term." });
            }
            finally
            {
                if (mustClose) await _connection.CloseAsync();
            }
        }

        /// <summary>
        /// DELETE /api/admin/synonyms/synonyms/{id}
        /// Deletes a synonym by ID.
        /// </summary>
        [HttpDelete("synonyms/{id:int}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> DeleteSynonym(int id, CancellationToken ct)
        {
            var mustClose = false;
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(ct);
                mustClose = true;
            }

            try
            {
                const string sql = @"DELETE FROM frl.frl_keywords_synonyms WHERE id = @id;";
                await using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("@id", id);

                var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);

                if (rowsAffected == 0)
                    return NotFound(new { Message = $"Synonym with ID {id} not found." });

                return NoContent();
            }
            finally
            {
                if (mustClose) await _connection.CloseAsync();
            }
        }

        #endregion

        #region CSV Import

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

        public sealed class MasterTermDto
        {
            public int Id { get; set; }
            public string MasterTerm { get; set; } = default!;
            public bool IsIncluded { get; set; }
        }

        public sealed class CreateMasterTermRequest
        {
            public string? MasterTerm { get; set; }
            public bool? IsIncluded { get; set; }
        }

        public sealed class UpdateMasterTermRequest
        {
            public string? MasterTerm { get; set; }
            public bool? IsIncluded { get; set; }
        }

        public sealed class SynonymDto
        {
            public int Id { get; set; }
            public int MasterId { get; set; }
            public string SynonymTerm { get; set; } = default!;
            public bool IsIncluded { get; set; }
        }

        public sealed class CreateSynonymRequest
        {
            public string? SynonymTerm { get; set; }
            public bool? IsIncluded { get; set; }
        }

        public sealed class UpdateSynonymRequest
        {
            public string? SynonymTerm { get; set; }
            public bool? IsIncluded { get; set; }
        }

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
