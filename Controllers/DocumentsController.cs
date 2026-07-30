using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using LocalRagAPI.Repositories;
using System.Linq;
using LocalRagAPI.Services;

namespace LocalRagAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentsController : ControllerBase
    {
        private readonly IDocumentRepository _docs;
        private readonly DocumentDeletionService _deletionService;

        public DocumentsController(IDocumentRepository docs, DocumentDeletionService deletionService)
        {
            _docs = docs;
            _deletionService = deletionService;
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

        // DELETE /api/documents/{id}
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(Guid id)
        {
            var userId = GetCurrentUserId();
            if (userId == Guid.Empty)
            {
                return Unauthorized();
            }

            var (success, message, statusCode) = await _deletionService.DeleteDocumentAsync(id, userId);

            if (!success)
            {
                if (statusCode == 404) return NotFound();
                if (statusCode == 403) return Forbid();
                return StatusCode(statusCode, new { message });
            }

            return Ok(new { success = true, message = "Document deleted successfully." });
        }
    }
}
