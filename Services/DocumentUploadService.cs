using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using LocalRagAPI.Models;
using LocalRagAPI.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalRagAPI.Services
{
    public class UploadResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty; // "Uploaded", "Duplicate", "Failed"
        public Guid? DocumentId { get; set; }
        public string JobId { get; set; } = string.Empty;
    }

    public class DocumentUploadService
    {
        private readonly IDocumentRepository _documentRepository;
        private readonly FileHashService _fileHashService;
        private readonly IngestionJobStore _jobStore;
        private readonly DocumentIngestionQueue _queue;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<DocumentUploadService> _logger;

        public DocumentUploadService(
            IDocumentRepository documentRepository,
            FileHashService fileHashService,
            IngestionJobStore jobStore,
            DocumentIngestionQueue queue,
            IWebHostEnvironment env,
            ILogger<DocumentUploadService> logger)
        {
            _documentRepository = documentRepository;
            _fileHashService = fileHashService;
            _jobStore = jobStore;
            _queue = queue;
            _env = env;
            _logger = logger;
        }

        public async Task<UploadResult> UploadAsync(Stream fileStream, string fileName, Guid userId, long fileLength)
        {
            if (fileStream == null || fileLength == 0)
                return new UploadResult { IsSuccess = false, Message = "Invalid file.", Status = "Failed" };

            if (fileLength > 5 * 1024 * 1024)
                return new UploadResult { IsSuccess = false, Message = "File too large. Max 5MB allowed.", Status = "Failed" };

            string fileHash;
            try
            {
                // Reset stream position if needed
                if (fileStream.CanSeek)
                    fileStream.Position = 0;

                fileHash = await _fileHashService.ComputeSha256Async(fileStream);
                _logger.LogInformation("Generated SHA-256 hash for uploaded file {FileName}: {HashPrefix}...", fileName, fileHash.Substring(0, 8));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate SHA-256 hash for file {FileName}", fileName);
                return new UploadResult { IsSuccess = false, Message = "Internal server error during file processing.", Status = "Failed" };
            }

            var existingByHash = await _documentRepository.GetByHashAsync(userId, fileHash);
            if (existingByHash != null)
            {
                _logger.LogWarning("Duplicate document detected. User {UserId} attempted to upload a file with hash {Hash}", userId, fileHash);
                return new UploadResult { IsSuccess = false, Message = "This document already exists.", Status = "Duplicate" };
            }

            string text;
            try
            {
                if (fileStream.CanSeek)
                    fileStream.Position = 0;

                if (fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(fileStream, Encoding.UTF8, true, 1024, true);
                    text = await reader.ReadToEndAsync();
                }
                else if (fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    using var document = UglyToad.PdfPig.PdfDocument.Open(fileStream, new UglyToad.PdfPig.ParsingOptions { UseLenientParsing = true });
                    var sb = new StringBuilder();
                    foreach (var page in document.GetPages())
                        sb.AppendLine(page.Text);
                    text = sb.ToString();
                }
                else
                {
                    return new UploadResult { IsSuccess = false, Message = "Unsupported file type. Only .txt and .pdf allowed.", Status = "Failed" };
                }

                if (string.IsNullOrWhiteSpace(text))
                    return new UploadResult { IsSuccess = false, Message = "File contains no readable text.", Status = "Failed" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading file {FileName}", fileName);
                return new UploadResult { IsSuccess = false, Message = $"Error reading file: {ex.Message}", Status = "Failed" };
            }

            // Create ingestion job metadata
            var jobId = Guid.NewGuid().ToString();
            var job = new IngestionJobStatus
            {
                JobId = jobId,
                State = IngestionJobState.Queued,
                CreatedAt = DateTime.UtcNow,
                CompletedBatches = 0,
                TotalBatches = 0
            };

            _jobStore.AddJob(job);

            var docEntity = new Document
            {
                UserId = userId,
                FileName = fileName,
                Sha256Hash = fileHash
            };

            var uploadsRoot = Path.Combine(_env.ContentRootPath, "uploads");
            var userFolder = Path.Combine(uploadsRoot, userId.ToString());
            Directory.CreateDirectory(userFolder);

            var ext = Path.GetExtension(fileName) ?? string.Empty;
            var diskFileName = docEntity.Id.ToString() + ext;
            var diskPath = Path.Combine(userFolder, diskFileName);

            try
            {
                if (fileStream.CanSeek)
                    fileStream.Position = 0;
                    
                await using (var fs = new FileStream(diskPath, FileMode.Create))
                {
                    await fileStream.CopyToAsync(fs);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save uploaded file {FileName}", fileName);
                return new UploadResult { IsSuccess = false, Message = "Failed to save uploaded file", Status = "Failed" };
            }

            docEntity.FilePath = diskPath;

            try
            {
                await _documentRepository.CreateAsync(docEntity);
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Unique constraint violation or concurrent insert for document hash {Hash} by user {UserId}", fileHash, userId);
                try { if (File.Exists(diskPath)) File.Delete(diskPath); } catch { }
                return new UploadResult { IsSuccess = false, Message = "This document already exists.", Status = "Duplicate" };
            }

            var request = new DocumentIngestionRequest
            {
                JobId = jobId,
                DocumentName = fileName,
                Text = text,
                FileName = fileName,
                DocumentId = docEntity.Id,
                UserId = docEntity.UserId
            };

            var enqueued = await _queue.EnqueueAsync(request, TimeSpan.FromSeconds(5));
            if (!enqueued)
            {
                _jobStore.MarkFailed(jobId, "Queue is full");
                return new UploadResult { IsSuccess = false, Message = "Server busy, try again later.", Status = "Failed" };
            }

            return new UploadResult 
            { 
                IsSuccess = true, 
                Message = "Document uploaded and queued for processing.", 
                Status = "Uploaded", 
                DocumentId = docEntity.Id, 
                JobId = jobId 
            };
        }
    }
}
