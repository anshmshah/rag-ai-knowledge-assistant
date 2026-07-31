using System;
using System.Threading.Tasks;
using LocalRagAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LocalRagAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class DemoController : ControllerBase
    {
        private readonly DemoKnowledgeBaseService _demoService;

        public DemoController(DemoKnowledgeBaseService demoService)
        {
            _demoService = demoService;
        }

        [HttpPost("initialize")]
        public async Task<IActionResult> InitializeDemo()
        {
            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrEmpty(userIdStr) || !Guid.TryParse(userIdStr, out var userId))
            {
                return Unauthorized(new { error = "Valid user ID is required to initialize demo." });
            }

            try
            {
                var result = await _demoService.InitializeDemoAsync(userId);
                return Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(403, new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "An error occurred while initializing the demo.", details = ex.Message });
            }
        }
    }
}
