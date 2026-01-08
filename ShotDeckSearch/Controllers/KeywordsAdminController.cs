using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;   // NEW
using Microsoft.Extensions.Logging;              // NEW
using Npgsql;
using NpgsqlTypes;
using ShotDeck.Keywords;
using ShotDeckSearch.Classes;
using System.Data;

namespace ShotDeckSearch.Controllers
{
    [ApiController]
    [Route("api/admin/keywords")]
    public sealed class KeywordsAdminController : ControllerBase
    {
        private readonly NpgsqlConnection _connection;
        private readonly IServiceScopeFactory _scopeFactory;                 // NEW
        private readonly ILogger<KeywordsAdminController> _logger;           // NEW

        // Note: you can remove IKeywordCacheService from constructor if not used elsewhere
        public KeywordsAdminController(
            NpgsqlConnection connection,
            IServiceScopeFactory scopeFactory,
            ILogger<KeywordsAdminController> logger)
        {
            _connection = connection;
            _scopeFactory = scopeFactory;     // NEW
            _logger = logger;                 // NEW
        }

        // Fire-and-forget cache refresh that uses a NEW scope (safe after request completes)
        private void QueueCacheRefresh()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var cache = scope.ServiceProvider.GetRequiredService<IKeywordCacheService>();
                    await cache.RefreshAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Keyword cache refresh failed.");
                }
            });
        }

        #region Categories

        // POST /api/admin/keywords/categories
        [HttpPost("categories")]
        public async Task<ActionResult<CategoryDto>> CreateCategory([FromBody] CreateCategoryRequest req, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return BadRequest("Name is required.");

            const string sql = @"
INSERT INTO frl.frl_keyword_categories (name)
VALUES (@name)
ON CONFLICT (name) DO NOTHING
RETURNING id, name;";

            var mustClose = false;
            if (_connection.State != ConnectionState.Open) { await _connection.OpenAsync(ct); mustClose = true; }

            try
            {
                await using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("@name", req.Name.Trim());

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var dto = new CategoryDto
                    {
                        Id = reader.GetInt32(0),
                        Name = reader.GetString(1),
                        Keywords = new()
                    };
                    QueueCacheRefresh(); // background
                    return CreatedAtAction(nameof(CreateCategory), new { dto.Id }, dto);
                }

                return Conflict($"Category '{req.Name}' already exists.");
            }
            finally { if (mustClose) await _connection.CloseAsync(); }
        }

        // PUT /api/admin/keywords/categories/{id}
        [HttpPut("categories/{categoryId:int}")]
        public async Task<ActionResult<CategoryDto>> UpdateCategory(int categoryId, [FromBody] UpdateCategoryRequest req, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return BadRequest("Name is required.");

            const string sql = @"
UPDATE frl.frl_keyword_categories
SET name = @name
WHERE id = @id
RETURNING id, name;";

            var mustClose = false;
            if (_connection.State != ConnectionState.Open) { await _connection.OpenAsync(ct); mustClose = true; }

            try
            {
                await using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("@id", categoryId);
                cmd.Parameters.AddWithValue("@name", req.Name.Trim());

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var dto = new CategoryDto
                    {
                        Id = reader.GetInt32(0),
                        Name = reader.GetString(1),
                        Keywords = new()
                    };
                    QueueCacheRefresh(); // background
                    return Ok(dto);
                }
                return NotFound();
            }
            catch (PostgresException ex) when (ex.SqlState == "23505") // unique_violation
            {
                return Conflict($"Category name '{req.Name}' already exists.");
            }
            finally { if (mustClose) await _connection.CloseAsync(); }
        }

        // DELETE /api/admin/keywords/categories/{id}?cascade=true
        [HttpDelete("categories/{categoryId:int}")]
        public async Task<IActionResult> DeleteCategory(int categoryId, [FromQuery] bool cascade = false, CancellationToken ct = default)
        {
            var mustClose = false;
            if (_connection.State != ConnectionState.Open) { await _connection.OpenAsync(ct); mustClose = true; }

            await using var tx = await _connection.BeginTransactionAsync(ct);
            try
            {
                // Count child keywords
                const string countSql = "SELECT COUNT(*) FROM frl.frl_keywords WHERE category_id = @id;";
                await using (var countCmd = new NpgsqlCommand(countSql, _connection, tx))
                {
                    countCmd.Parameters.AddWithValue("@id", categoryId);
                    var count = (long)(await countCmd.ExecuteScalarAsync(ct) ?? 0L);

                    if (count > 0 && !cascade)
                        return Conflict($"Category has {count} keywords. Pass ?cascade=true to delete them as well.");
                }

                if (cascade)
                {
                    const string delKeywords = "DELETE FROM frl.frl_keywords WHERE category_id = @id;";
                    await using var dk = new NpgsqlCommand(delKeywords, _connection, tx);
                    dk.Parameters.AddWithValue("@id", categoryId);
                    await dk.ExecuteNonQueryAsync(ct);
                }

                const string delCategory = "DELETE FROM frl.frl_keyword_categories WHERE id = @id;";
                await using (var dc = new NpgsqlCommand(delCategory, _connection, tx))
                {
                    dc.Parameters.AddWithValue("@id", categoryId);
                    await dc.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                QueueCacheRefresh(); // background
                return NoContent();
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
            finally { if (mustClose) await _connection.CloseAsync(); }
        }

        #endregion

        #region Keywords

        // POST /api/admin/keywords
        [HttpPost]
        public async Task<ActionResult<KeywordDto>> CreateKeyword([FromBody] CreateKeywordRequest req, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(req.Keyword))
                return BadRequest("Keyword is required.");

            var mustClose = false;
            if (_connection.State != ConnectionState.Open) { await _connection.OpenAsync(ct); mustClose = true; }

            try
            {
                // Ensure category exists
                const string catSql = "SELECT 1 FROM frl.frl_keyword_categories WHERE id = @id;";
                await using (var catCmd = new NpgsqlCommand(catSql, _connection))
                {
                    catCmd.Parameters.AddWithValue("@id", req.CategoryId);
                    var exists = await catCmd.ExecuteScalarAsync(ct) is not null;
                    if (!exists) return NotFound($"Category {req.CategoryId} not found.");
                }

                const string insSql = @"
INSERT INTO frl.frl_keywords (keyword, category_id, is_included)
VALUES (@kw, @cat, @inc)
RETURNING id, keyword, is_included, created_at;";

                await using var cmd = new NpgsqlCommand(insSql, _connection);
                cmd.Parameters.AddWithValue("@kw", req.Keyword.Trim());
                cmd.Parameters.AddWithValue("@cat", req.CategoryId);
                cmd.Parameters.Add("@inc", NpgsqlDbType.Boolean).Value = req.IsIncluded ?? true;

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);

                var dto = new KeywordDto
                {
                    Id = reader.GetInt32(0),
                    Keyword = reader.GetString(1),
                    IsIncluded = reader.GetBoolean(2),
                    CreatedAt = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)
                };

                QueueCacheRefresh(); // background
                return Created($"/api/admin/keywords/{dto.Id}", dto);
            }
            finally { if (mustClose) await _connection.CloseAsync(); }
        }

        // PUT /api/admin/keywords/{id}
        [HttpPut("{keywordId:int}")]
        public async Task<ActionResult<KeywordDto>> UpdateKeyword(int keywordId, [FromBody] UpdateKeywordRequest req, CancellationToken ct)
        {
            if (req.Keyword is not null && string.IsNullOrWhiteSpace(req.Keyword))
                return BadRequest("Keyword cannot be empty.");

            var mustClose = false;
            if (_connection.State != ConnectionState.Open) { await _connection.OpenAsync(ct); mustClose = true; }

            await using var tx = await _connection.BeginTransactionAsync(ct);
            try
            {
                // Build dynamic SET list
                var sets = new List<string>();
                if (req.Keyword is not null) sets.Add("keyword = @kw");
                if (req.IsIncluded is not null) sets.Add("is_included = @inc");
                if (req.CategoryId is not null) sets.Add("category_id = @cat");
                if (sets.Count == 0) return BadRequest("No fields to update.");

                var sql = $@"
UPDATE frl.frl_keywords
SET {string.Join(", ", sets)}
WHERE id = @id
RETURNING id, keyword, is_included, created_at;";

                KeywordDto? dto = null;

                await using (var cmd = new NpgsqlCommand(sql, _connection, tx))
                {
                    cmd.Parameters.AddWithValue("@id", keywordId);
                    if (req.Keyword is not null) cmd.Parameters.AddWithValue("@kw", req.Keyword.Trim());
                    if (req.IsIncluded is not null) cmd.Parameters.Add("@inc", NpgsqlDbType.Boolean).Value = req.IsIncluded.Value;
                    if (req.CategoryId is not null) cmd.Parameters.AddWithValue("@cat", req.CategoryId.Value);

                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        dto = new KeywordDto
                        {
                            Id = reader.GetInt32(0),
                            Keyword = reader.GetString(1),
                            IsIncluded = reader.GetBoolean(2),
                            CreatedAt = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)
                        };
                    }
                    else
                    {
                        await tx.RollbackAsync(ct);
                        return NotFound();
                    }
                } // reader disposed here

                await tx.CommitAsync(ct);

                QueueCacheRefresh(); // background
                return Ok(dto);
            }
            finally
            {
                if (mustClose) await _connection.CloseAsync();
            }
        }

        // DELETE /api/admin/keywords/{id}
        [HttpDelete("{keywordId:int}")]
        public async Task<IActionResult> DeleteKeyword(int keywordId, CancellationToken ct)
        {
            const string sql = "DELETE FROM frl.frl_keywords WHERE id = @id;";

            var mustClose = false;
            if (_connection.State != ConnectionState.Open) { await _connection.OpenAsync(ct); mustClose = true; }

            try
            {
                await using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("@id", keywordId);
                await cmd.ExecuteNonQueryAsync(ct);

                QueueCacheRefresh(); // background
                return NoContent();
            }
            finally { if (mustClose) await _connection.CloseAsync(); }
        }

        // READS (kept from your post)

        [HttpGet("categories-and-keywords")]
        [ProducesResponseType(typeof(List<CategoryDto>), StatusCodes.Status200OK)]
        public async Task<ActionResult<List<CategoryDto>>> GetCategoriesWithKeywords(
            [FromQuery] string? isIncluded = "both",
            [FromQuery] bool includeEmptyCategories = true,
            CancellationToken ct = default)
        {
            bool? includeFilter = isIncluded?.ToLowerInvariant() switch
            {
                "true" => true,
                "false" => false,
                "both" or null => null,
                _ => null
            };

            const string sql = @"
SELECT
    c.id            AS category_id,
    c.name          AS category_name,
    k.id            AS keyword_id,
    k.keyword       AS keyword_text,
    k.is_included   AS keyword_is_included,
    k.created_at    AS keyword_created_at
FROM frl.frl_keyword_categories c
LEFT JOIN frl.frl_keywords k
    ON k.category_id = c.id
   AND (@is_included IS NULL OR k.is_included = @is_included)
ORDER BY c.id, k.keyword ASC NULLS LAST;";

            var categories = new List<CategoryDto>();
            var byId = new Dictionary<int, CategoryDto>();

            var mustClose = false;
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(ct);
                mustClose = true;
            }

            try
            {
                await using var cmd = new NpgsqlCommand(sql, _connection);
                var p = cmd.Parameters.Add("@is_included", NpgsqlDbType.Boolean);
                p.Value = includeFilter is null ? DBNull.Value : includeFilter;

                await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
                while (await reader.ReadAsync(ct))
                {
                    var categoryId = reader.GetInt32(reader.GetOrdinal("category_id"));
                    if (!byId.TryGetValue(categoryId, out var category))
                    {
                        category = new CategoryDto
                        {
                            Id = categoryId,
                            Name = reader.GetString(reader.GetOrdinal("category_name")),
                            Keywords = new List<KeywordDto>()
                        };
                        byId.Add(categoryId, category);
                        categories.Add(category);
                    }

                    var keywordIdOrd = reader.GetOrdinal("keyword_id");
                    if (!reader.IsDBNull(keywordIdOrd))
                    {
                        category.Keywords.Add(new KeywordDto
                        {
                            Id = reader.GetInt32(keywordIdOrd),
                            Keyword = reader.GetString(reader.GetOrdinal("keyword_text")),
                            IsIncluded = reader.IsDBNull(reader.GetOrdinal("keyword_is_included"))
                                         ? (bool?)null
                                         : reader.GetBoolean(reader.GetOrdinal("keyword_is_included")),
                            CreatedAt = reader.IsDBNull(reader.GetOrdinal("keyword_created_at"))
                                        ? (DateTimeOffset?)null
                                        : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("keyword_created_at"))
                        });
                    }
                }
            }
            finally
            {
                if (mustClose) await _connection.CloseAsync();
            }

            if (!includeEmptyCategories)
                categories = categories.Where(c => c.Keywords.Count > 0).ToList();

            return Ok(categories);
        }

        [HttpGet("categories-and-keywords/{categoryId:int}")]
        [ProducesResponseType(typeof(CategoryDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<CategoryDto>> GetCategoryById(
            int categoryId,
            [FromQuery] string? isIncluded = "both",
            CancellationToken ct = default)
        {
            bool? includeFilter = isIncluded?.ToLowerInvariant() switch
            {
                "true" => true,
                "false" => false,
                "both" or null => null,
                _ => null
            };

            const string sql = @"
SELECT
    c.id            AS category_id,
    c.name          AS category_name,
    k.id            AS keyword_id,
    k.keyword       AS keyword_text,
    k.is_included   AS keyword_is_included,
    k.created_at    AS keyword_created_at
FROM frl.frl_keyword_categories c
LEFT JOIN frl.frl_keywords k
    ON k.category_id = c.id
   AND (@is_included IS NULL OR k.is_included = @is_included)
WHERE c.id = @category_id
ORDER BY k.keyword ASC NULLS LAST;";

            var mustClose = false;
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(ct);
                mustClose = true;
            }

            CategoryDto? category = null;

            try
            {
                await using var cmd = new NpgsqlCommand(sql, _connection);
                var p = cmd.Parameters.Add("@is_included", NpgsqlDbType.Boolean);
                p.Value = includeFilter is null ? DBNull.Value : includeFilter;

                cmd.Parameters.AddWithValue("@category_id", categoryId);

                await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
                while (await reader.ReadAsync(ct))
                {
                    category ??= new CategoryDto
                    {
                        Id = reader.GetInt32(reader.GetOrdinal("category_id")),
                        Name = reader.GetString(reader.GetOrdinal("category_name")),
                        Keywords = new List<KeywordDto>()
                    };

                    var keywordIdOrd = reader.GetOrdinal("keyword_id");
                    if (!reader.IsDBNull(keywordIdOrd))
                    {
                        category.Keywords.Add(new KeywordDto
                        {
                            Id = reader.GetInt32(keywordIdOrd),
                            Keyword = reader.GetString(reader.GetOrdinal("keyword_text")),
                            IsIncluded = reader.IsDBNull(reader.GetOrdinal("keyword_is_included"))
                                         ? (bool?)null
                                         : reader.GetBoolean(reader.GetOrdinal("keyword_is_included")),
                            CreatedAt = reader.IsDBNull(reader.GetOrdinal("keyword_created_at"))
                                        ? (DateTimeOffset?)null
                                        : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("keyword_created_at"))
                        });
                    }
                }
            }
            finally
            {
                if (mustClose) await _connection.CloseAsync();
            }

            return category is null ? NotFound() : Ok(category);
        }

        #endregion

        #region DTOs
        public sealed class CreateCategoryRequest { public string Name { get; set; } = default!; }
        public sealed class UpdateCategoryRequest { public string Name { get; set; } = default!; }
        public sealed class CreateKeywordRequest { public string Keyword { get; set; } = default!; public int CategoryId { get; set; } public bool? IsIncluded { get; set; } }
        public sealed class UpdateKeywordRequest { public string? Keyword { get; set; } public int? CategoryId { get; set; } public bool? IsIncluded { get; set; } }
        public sealed class CategoryDto { public int Id { get; set; } public string Name { get; set; } = default!; public List<KeywordDto> Keywords { get; set; } = new(); }
        public sealed class KeywordDto { public int Id { get; set; } public string Keyword { get; set; } = default!; public bool? IsIncluded { get; set; } public DateTimeOffset? CreatedAt { get; set; } }
        #endregion
    }
}
