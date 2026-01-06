using Microsoft.AspNetCore.Mvc;
using Semantic_Text_to_Vector.Services;

namespace Semantic_Text_to_Vector.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class R2FoldersController : ControllerBase
    {
        private readonly IR2StorageService _r2StorageService;
        private readonly ILogger<R2FoldersController> _logger;

        public R2FoldersController(IR2StorageService r2StorageService, ILogger<R2FoldersController> logger)
        {
            _r2StorageService = r2StorageService;
            _logger = logger;
        }

        [HttpGet(Name = "GetR2Folders")]
        public async Task<ActionResult<IEnumerable<string>>> Get()
        {
            try
            {
                var folders = await _r2StorageService.GetFoldersAsync();
                return Ok(folders);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving folders from R2 storage");
                return StatusCode(500, "An error occurred while retrieving folders from R2 storage");
            }
        }
    }
}
