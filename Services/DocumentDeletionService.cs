using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LocalRagAPI.Repositories;
using Microsoft.Extensions.Logging;

namespace LocalRagAPI.Services
{
    public class DocumentDeletionService
    {
        private readonly IDocumentRepository _documentRepository;
        private readonly QdrantService _qdrantService;
        private readonly ILogger<DocumentDeletionService> _logger;

        public DocumentDeletionService(
            IDocumentRepository documentRepository,
            QdrantService qdrantService,
            ILogger<DocumentDeletionService> logger)
        {
            _documentRepository = documentRepository;
            _qdrantService = qdrantService;
            _logger = logger;
        }

        public async Task<(bool Success, string Message, int StatusCode)> DeleteDocumentAsync(Guid documentId, Guid currentUserId)
        {
            var doc = await _documentRepository.GetByIdAsync(documentId);
            
            if (doc == null)
            {
                return (false, "Document not found.", 404);
            }

            if (doc.UserId != currentUserId)
            {
                return (false, "Unauthorized access to document.", 403);
            }

            try
            {
                // Delete vectors from Qdrant using document_id
                await _qdrantService.DeleteByDocumentIdAsync(documentId.ToString());
                _logger.LogInformation("Deleted Qdrant vectors for document {DocumentId}", documentId);

                // Delete physical uploaded file
                if (!string.IsNullOrEmpty(doc.FilePath) && File.Exists(doc.FilePath))
                {
                    File.Delete(doc.FilePath);
                    _logger.LogInformation("Deleted physical file for document {DocumentId} at {FilePath}", documentId, doc.FilePath);

                    // Check if uploads/{userId} directory is empty and remove if so
                    var directory = Path.GetDirectoryName(doc.FilePath);
                    if (directory != null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory);
                        _logger.LogInformation("Removed empty directory {Directory}", directory);
                    }
                }
                
                // Mark document deleted in PostgreSQL
                await _documentRepository.MarkDeletedAsync(documentId);
                _logger.LogInformation("Marked document {DocumentId} as deleted in PostgreSQL", documentId);

                return (true, "Document deleted successfully.", 200);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete document {DocumentId}", documentId);
                return (false, "An error occurred while deleting the document.", 500);
            }
        }
    }
}
