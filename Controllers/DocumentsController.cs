using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using LocalRagAPI.Repositories;
using System.Linq;

namespace LocalRagAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentsController : ControllerBase
    {
        private readonly IDocumentRepository _docs;

        public DocumentsController(IDocumentRepository docs)
        {
            _docs = docs;
        }

        private Guid GetCurrentUserId()
        {
            if (User?.Identity?.IsAuthenticated == true)
            {
                var sub = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                          ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

                if (Guid.TryParse(sub, out var parsed)) return parsed;
            }

            return Guid.Empty;
        }

        // GET /api/documents
        [HttpGet]
        public async Task<IActionResult> List()
        {
            var userId = GetCurrentUserId();
            var list = await _docs.ListByUserAsync(userId);

            var result = list.Select(d => new { id = d.Id, fileName = d.FileName, uploadedAt = d.UploadedAt });
            return Ok(result);
        }
    }
}
