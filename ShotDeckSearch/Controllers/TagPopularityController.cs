using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using System.Data;

namespace ShotDeckSearch.Controllers
{
    [ApiController]
    [Route("api/admin/tag-popularity")]
    public sealed class TagPopularityController : ControllerBase
    {
        private readonly NpgsqlConnection _connection;
        private readonly ILogger<TagPopularityController> _logger;

        public TagPopularityController(
            NpgsqlConnection connection,
            ILogger<TagPopularityController> logger)
        {
            _connection = connection;
            _logger = logger;
        }

        /// <summary>
        /// GET /api/admin/tag-popularity
        /// Returns all tag popularity rules from the database.
        /// </summary>
        [HttpGet]
        [ProducesResponseType(typeof(List<TagPopularityRuleDto>), StatusCodes.Status200OK)]
        public async Task<ActionResult<List<TagPopularityRuleDto>>> GetAll(CancellationToken ct)
        {
            var mustClose = false;
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(ct);
                mustClose = true;
            }

            try
            {
                const string sql = @"
SELECT id, tag, percentage, is_active, created_at, updated_at
FROM frl.frl_popularity_tag_rules
ORDER BY tag;";

                await using var cmd = new NpgsqlCommand(sql, _connection);
                await using var reader = await cmd.ExecuteReaderAsync(ct);

                var results = new List<TagPopularityRuleDto>();
                while (await reader.ReadAsync(ct))
                {
                    results.Add(MapToDto(reader));
                }

                return Ok(results);
            }
            finally
            {
                if (mustClose) await _connection.CloseAsync();
            }
        }

        /// <summary>
        /// GET /api/admin/tag-popularity/{id}
        /// Returns a single tag popularity rule by ID.
        /// </summary>
        [HttpGet("{id:long}")]
        [ProducesResponseType(typeof(TagPopularityRuleDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<TagPopularityRuleDto>> GetById(long id, CancellationToken ct)
        {
            var mustClose = false;
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(ct);
                mustClose = true;
            }

            try
            {
                const string sql = @"
SELECT id, tag, percentage, is_active, created_at, updated_at
FROM frl.frl_popularity_tag_rules
WHERE id = @id;";

                await using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("@id", id);
                await using var reader = await cmd.ExecuteReaderAsync(ct);

                if (!await reader.ReadAsync(ct))
                    return NotFound(new { Message = $"Tag popularity rule with ID {id} not found." });

                return Ok(MapToDto(reader));
            }
            finally
            {
                if (mustClose) await _connection.CloseAsync();
            }
        }

        /// <summary>
        /// POST /api/admin/tag-popularity
        /// Creates a new tag popularity rule.
        /// </summary>
        [HttpPost]
        [ProducesResponseType(typeof(TagPopularityRuleDto), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<TagPopularityRuleDto>> Create([FromBody] CreateTagPopularityRuleRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.Tag))
                return BadRequest(new { Message = "Tag is required." });

            if (request.Percentage < -100 || request.Percentage > 1000)
                return BadRequest(new { Message = "Percentage must be between -100 and 1000." });

            var mustClose = false;
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(ct);
                mustClose = true;
            }

            try
            {
                const string sql = @"
INSERT INTO frl.frl_popularity_tag_rules (tag, percentage, is_active, created_at, updated_at)
VALUES (@tag, @percentage, @is_active, @created_at, @updated_at)
RETURNING id, tag, percentage, is_active, created_at, updated_at;";

                var now = DateTime.UtcNow;

                await using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("@tag", request.Tag.Trim());
                cmd.Parameters.AddWithValue("@percentage", request.Percentage);
                cmd.Parameters.AddWithValue("@is_active", request.IsActive ?? true);
                cmd.Parameters.AddWithValue("@created_at", request.CreatedAt ?? now);
                cmd.Parameters.AddWithValue("@updated_at", request.UpdatedAt ?? now);

                await using var reader = await cmd.ExecuteReaderAsync(ct);

                if (!await reader.ReadAsync(ct))
                    return BadRequest(new { Message = "Failed to create tag popularity rule." });

                var result = MapToDto(reader);
                return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                return Conflict(new { Message = $"A tag popularity rule with tag '{request.Tag}' already exists." });
            }
            finally
            {
                if (mustClose) await _connection.CloseAsync();
            }
        }

        /// <summary>
        /// PUT /api/admin/tag-popularity/{id}
        /// Updates an existing tag popularity rule.
        /// </summary>
        [HttpPut("{id:long}")]
        [ProducesResponseType(typeof(TagPopularityRuleDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<TagPopularityRuleDto>> Update(long id, [FromBody] UpdateTagPopularityRuleRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.Tag))
                return BadRequest(new { Message = "Tag is required." });

            if (request.Percentage < -100 || request.Percentage > 1000)
                return BadRequest(new { Message = "Percentage must be between -100 and 1000." });

            var mustClose = false;
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(ct);
                mustClose = true;
            }

            try
            {
                const string sql = @"
UPDATE frl.frl_popularity_tag_rules
SET tag = @tag,
    percentage = @percentage,
    is_active = @is_active,
    updated_at = @updated_at
WHERE id = @id
RETURNING id, tag, percentage, is_active, created_at, updated_at;";

                await using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@tag", request.Tag.Trim());
                cmd.Parameters.AddWithValue("@percentage", request.Percentage);
                cmd.Parameters.AddWithValue("@is_active", request.IsActive ?? true);
                cmd.Parameters.AddWithValue("@updated_at", request.UpdatedAt ?? DateTime.UtcNow);

                await using var reader = await cmd.ExecuteReaderAsync(ct);

                if (!await reader.ReadAsync(ct))
                    return NotFound(new { Message = $"Tag popularity rule with ID {id} not found." });

                return Ok(MapToDto(reader));
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                return Conflict(new { Message = $"A tag popularity rule with tag '{request.Tag}' already exists." });
            }
            finally
            {
                if (mustClose) await _connection.CloseAsync();
            }
        }

        /// <summary>
        /// DELETE /api/admin/tag-popularity/{id}
        /// Deletes a tag popularity rule by ID.
        /// </summary>
        [HttpDelete("{id:long}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Delete(long id, CancellationToken ct)
        {
            var mustClose = false;
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(ct);
                mustClose = true;
            }

            try
            {
                const string sql = @"DELETE FROM frl.frl_popularity_tag_rules WHERE id = @id;";
                await using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("@id", id);

                var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);

                if (rowsAffected == 0)
                    return NotFound(new { Message = $"Tag popularity rule with ID {id} not found." });

                return NoContent();
            }
            finally
            {
                if (mustClose) await _connection.CloseAsync();
            }
        }

        #region Helpers

        private static TagPopularityRuleDto MapToDto(NpgsqlDataReader reader)
        {
            return new TagPopularityRuleDto
            {
                Id = reader.GetInt64(0),
                Tag = reader.GetString(1),
                Percentage = reader.GetInt32(2),
                IsActive = reader.GetBoolean(3),
                CreatedAt = reader.GetDateTime(4),
                UpdatedAt = reader.GetDateTime(5)
            };
        }

        #endregion

        #region DTOs

        public sealed class TagPopularityRuleDto
        {
            public long Id { get; set; }
            public string Tag { get; set; } = default!;
            public int Percentage { get; set; }
            public bool IsActive { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime UpdatedAt { get; set; }
        }

        public sealed class CreateTagPopularityRuleRequest
        {
            public string? Tag { get; set; }
            public int Percentage { get; set; }
            public bool? IsActive { get; set; }
            public DateTime? CreatedAt { get; set; }
            public DateTime? UpdatedAt { get; set; }
        }

        public sealed class UpdateTagPopularityRuleRequest
        {
            public string? Tag { get; set; }
            public int Percentage { get; set; }
            public bool? IsActive { get; set; }
            public DateTime? UpdatedAt { get; set; }
        }

        #endregion
    }
}
