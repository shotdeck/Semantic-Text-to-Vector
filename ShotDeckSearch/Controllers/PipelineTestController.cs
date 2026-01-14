using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ShotDeckSearch.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class PipelineTestController : ControllerBase
    {
        // Hardcoded toggle – you can make this read from config or env var
        private const bool ShouldPass = false;

        [HttpGet("health")]
        public IActionResult HealthCheck()
        {
            // Always returns 200 to verify the app runs
            return Ok(new
            {
                status = "healthy",
                time = DateTime.UtcNow
            });
        }

        [HttpGet("test")]
        public IActionResult RunPipelineTest()
        {
            // Example: simulate API test success or failure
            if (!ShouldPass)
            {
                // Return HTTP 500 so pipeline fails
                return StatusCode(500, new
                {
                    result = "fail",
                    reason = "Simulated failure for pipeline testing"
                });
            }

            // Otherwise return 200 OK
            return Ok(new
            {
                result = "pass",
                message = "All pipeline checks passed successfully"
            });
        }
    }
}