using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LocalRagAPI.Models;
using LocalRagAPI.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LocalRagAPI.Services
{
    public class DemoInitResponse
    {
        public bool AlreadyInitialized { get; set; }
        public int Uploaded { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public List<DemoDocumentStatus> Documents { get; set; } = new List<DemoDocumentStatus>();
        public List<string> SuggestedQuestions { get; set; } = new List<string>();
    }

    public class DemoDocumentStatus
    {
        public string Name { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    public class DemoKnowledgeBaseService
    {
        private readonly DocumentUploadService _uploadService;
        private readonly IDocumentRepository _documentRepository;
        private readonly FileHashService _fileHashService;
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;
        private readonly ILogger<DemoKnowledgeBaseService> _logger;

        public DemoKnowledgeBaseService(
            DocumentUploadService uploadService,
            IDocumentRepository documentRepository,
            FileHashService fileHashService,
            IWebHostEnvironment env,
            IConfiguration config,
            ILogger<DemoKnowledgeBaseService> logger)
        {
            _uploadService = uploadService;
            _documentRepository = documentRepository;
            _fileHashService = fileHashService;
            _env = env;
            _config = config;
            _logger = logger;
        }

        public async Task<DemoInitResponse> InitializeDemoAsync(Guid userId)
        {
            var isEnabled = _config.GetValue<bool>("DemoKnowledgeBase:Enabled", true);
            if (!isEnabled)
            {
                throw new InvalidOperationException("Demo Knowledge Base initialization is disabled.");
            }

            var folderName = _config.GetValue<string>("DemoKnowledgeBase:Folder") ?? "DemoDocuments";
            var demoFolder = Path.Combine(_env.ContentRootPath, folderName);
            var configuredQuestions = _config.GetSection("DemoKnowledgeBase:SuggestedQuestions").Get<List<string>>();

            var response = new DemoInitResponse
            {
                SuggestedQuestions = configuredQuestions ?? new List<string>()
            };

            if (!Directory.Exists(demoFolder))
            {
                _logger.LogWarning("Demo folder '{Folder}' does not exist.", demoFolder);
                return response;
            }

            var files = Directory.GetFiles(demoFolder, "*.*")
                .Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!files.Any())
            {
                _logger.LogWarning("No PDF or TXT files found in '{Folder}'.", demoFolder);
                return response;
            }

            // Quick early-exit check using SHA-256 hashes
            var fileHashes = new Dictionary<string, string>(); // path -> hash
            var hashesToSearch = new HashSet<string>();

            foreach (var file in files)
            {
                try
                {
                    await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var hash = await _fileHashService.ComputeSha256Async(fs);
                    fileHashes[file] = hash;
                    hashesToSearch.Add(hash);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to hash demo file {File}", file);
                }
            }

            if (hashesToSearch.Any())
            {
                var existingHashes = await _documentRepository.GetExistingHashesAsync(userId, hashesToSearch);
                
                if (hashesToSearch.All(h => existingHashes.Contains(h)))
                {
                    response.AlreadyInitialized = true;
                    foreach (var file in files)
                    {
                        response.Skipped++;
                        response.Documents.Add(new DemoDocumentStatus { Name = Path.GetFileName(file), Status = "Skipped" });
                    }
                    return response;
                }
            }

            foreach (var filePath in files)
            {
                var fileName = Path.GetFileName(filePath);
                try
                {
                    await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    
                    var result = await _uploadService.UploadAsync(fileStream, fileName, userId, fileStream.Length);
                    
                    response.Documents.Add(new DemoDocumentStatus { Name = fileName, Status = result.Status });
                    
                    if (result.Status == "Uploaded")
                        response.Uploaded++;
                    else if (result.Status == "Duplicate")
                        response.Skipped++;
                    else
                        response.Failed++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to upload demo document {FileName}", fileName);
                    response.Documents.Add(new DemoDocumentStatus { Name = fileName, Status = "Failed" });
                    response.Failed++;
                }
            }

            _logger.LogInformation("Demo init complete for {UserId}. Uploaded: {U}, Skipped: {S}, Failed: {F}", userId, response.Uploaded, response.Skipped, response.Failed);
            
            return response;
        }
    }
}
