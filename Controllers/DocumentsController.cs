using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using LocalRagAPI.Repositories;
using System.Linq;
using System.IO;
using LocalRagAPI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalRagAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentsController : ControllerBase
    {
        private readonly IDocumentRepository _docs;
        private readonly DocumentDeletionService _deletionService;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<DocumentsController> _logger;

        public DocumentsController(
            IDocumentRepository docs, 
            DocumentDeletionService deletionService,
            IWebHostEnvironment env,
            ILogger<DocumentsController> logger)
        {
            _docs = docs;
            _deletionService = deletionService;
            _env = env;
            _logger = logger;
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

        // GET /api/documents/{id}/preview
        [HttpGet("{id}/preview")]
        public Task<IActionResult> Preview(Guid id)
        {
            return GetDocumentFileResultAsync(id, isDownload: false);
        }

        // GET /api/documents/{id}/download
        [HttpGet("{id}/download")]
        public Task<IActionResult> Download(Guid id)
        {
            return GetDocumentFileResultAsync(id, isDownload: true);
        }

        private async Task<IActionResult> GetDocumentFileResultAsync(Guid id, bool isDownload)
        {
            var userId = GetCurrentUserId();
            if (userId == Guid.Empty)
            {
                return Unauthorized();
            }

            var doc = await _docs.GetByIdAsync(id);
            if (doc == null)
            {
                return NotFound(new { message = "Document not found." });
            }

            if (doc.UserId != userId)
            {
                return StatusCode(403, new { message = "Unauthorized access to document." });
            }

            if (!doc.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var operation = isDownload ? "Download" : "Preview";
                return BadRequest(new { message = $"{operation} is only supported for PDF files." });
            }

            if (string.IsNullOrEmpty(doc.FilePath) || !System.IO.File.Exists(doc.FilePath))
            {
                return NotFound(new { message = "File not found on disk." });
            }

            var uploadsRoot = Path.GetFullPath(Path.Combine(_env.ContentRootPath, "uploads"));
            var resolvedFilePath = Path.GetFullPath(doc.FilePath);
            
            if (!resolvedFilePath.StartsWith(uploadsRoot, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Path traversal attempt detected for document {DocumentId}", id);
                return StatusCode(403, new { message = "Invalid file path." });
            }

            if (isDownload)
            {
                _logger.LogInformation("Successfully downloaded document {DocumentId}", id);
                Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{doc.FileName}\"");
            }
            else
            {
                _logger.LogInformation("Successfully previewed document {DocumentId}", id);
                Response.Headers.Append("Content-Disposition", $"inline; filename=\"{doc.FileName}\"");
            }

            Response.Headers.Append("Cache-Control", "private, no-cache");
            
            var stream = new FileStream(resolvedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return File(stream, "application/pdf", enableRangeProcessing: true);
        }
    }
}
