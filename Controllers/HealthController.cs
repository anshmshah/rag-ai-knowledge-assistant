using Microsoft.AspNetCore.Mvc;

namespace LocalRagAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class HealthController : ControllerBase
    {
        private readonly LocalRagAPI.Services.HealthService _healthService;

        public HealthController(LocalRagAPI.Services.HealthService healthService)
        {
            _healthService = healthService;
        }

        [HttpGet]
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        public async Task<IActionResult> Get()
        {
            var health = await _healthService.GetHealthAsync();
            
            var status = health.GetType().GetProperty("status")?.GetValue(health)?.ToString();
            
            if (status == "Unhealthy")
            {
                return StatusCode(503, health);
            }

            return Ok(health);
        }
    }
}
