using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using LocalRagAPI.Data;
using LocalRagAPI.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LocalRagAPI.Services
{
    public class HealthService
    {
        private readonly ApplicationDbContext _db;
        private readonly QdrantService _qdrant;
        private readonly IWebHostEnvironment _env;
        private readonly WorkerStatus _workerStatus;
        private readonly IConfiguration _config;
        private readonly ILogger<HealthService> _logger;

        public HealthService(
            ApplicationDbContext db,
            QdrantService qdrant,
            IWebHostEnvironment env,
            WorkerStatus workerStatus,
            IConfiguration config,
            ILogger<HealthService> logger)
        {
            _db = db;
            _qdrant = qdrant;
            _env = env;
            _workerStatus = workerStatus;
            _config = config;
            _logger = logger;
        }

        public async Task<object> GetHealthAsync()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";

            var dbTask = CheckDatabaseAsync();
            var qdrantTask = CheckQdrantAsync();
            var storageTask = CheckStorageAsync();
            
            // Wait for all asynchronous checks
            await Task.WhenAll(dbTask, qdrantTask, storageTask);

            var dbStatus = await dbTask;
            var qdrantStatus = await qdrantTask;
            var storageStatus = await storageTask;
            var workerStatus = _workerStatus.IsRunning ? "Healthy" : "Unhealthy";
            var configStatus = CheckConfiguration();

            var isHealthy = dbStatus == "Healthy" && 
                            qdrantStatus == "Healthy" && 
                            storageStatus == "Healthy" && 
                            workerStatus == "Healthy" && 
                            configStatus.Status == "Healthy";

            return new
            {
                status = isHealthy ? "Healthy" : "Unhealthy",
                timestamp = DateTime.UtcNow,
                version = version,
                checks = new
                {
                    api = "Healthy",
                    database = dbStatus,
                    qdrant = qdrantStatus,
                    storage = storageStatus,
                    backgroundWorker = workerStatus,
                    configuration = configStatus
                }
            };
        }

        private async Task<string> CheckDatabaseAsync()
        {
            try
            {
                var canConnect = await _db.Database.CanConnectAsync();
                return canConnect ? "Healthy" : "Unhealthy";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HealthCheck: Database check failed.");
                return "Unhealthy";
            }
        }

        private async Task<string> CheckQdrantAsync()
        {
            try
            {
                var isReachable = await _qdrant.PingAsync();
                return isReachable ? "Healthy" : "Unhealthy";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HealthCheck: Qdrant check failed.");
                return "Unhealthy";
            }
        }

        private Task<string> CheckStorageAsync()
        {
            try
            {
                var uploadsRoot = Path.Combine(_env.ContentRootPath, "uploads");
                if (!Directory.Exists(uploadsRoot))
                {
                    Directory.CreateDirectory(uploadsRoot);
                }

                var tempFile = Path.Combine(uploadsRoot, $"{Guid.NewGuid()}.tmp");

                try
                {
                    // Write test
                    File.WriteAllText(tempFile, "healthcheck");

                    // Read test
                    var content = File.ReadAllText(tempFile);
                    if (content != "healthcheck")
                    {
                        throw new Exception("Read content does not match written content.");
                    }
                }
                finally
                {
                    // Delete test
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }

                return Task.FromResult("Healthy");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HealthCheck: Storage check failed.");
                return Task.FromResult("Unhealthy");
            }
        }

        private ConfigCheckResult CheckConfiguration()
        {
            var missingKeys = new List<string>();

            var requiredKeys = new[]
            {
                "ConnectionStrings:Default",
                "Jwt:Key",
                "Qdrant:Url",
                "Qdrant:ApiKey",
                "Groq:ApiKey",
                "Jina:ApiKey"
            };

            foreach (var key in requiredKeys)
            {
                if (string.IsNullOrWhiteSpace(_config[key]))
                {
                    missingKeys.Add(key);
                }
            }

            if (missingKeys.Count > 0)
            {
                _logger.LogWarning("HealthCheck: Missing configuration keys: {Keys}", string.Join(", ", missingKeys));
                return new ConfigCheckResult
                {
                    Status = "Unhealthy",
                    MissingKeys = missingKeys
                };
            }

            return new ConfigCheckResult
            {
                Status = "Healthy",
                MissingKeys = Array.Empty<string>()
            };
        }

        public class ConfigCheckResult
        {
            public string Status { get; set; }
            public IEnumerable<string> MissingKeys { get; set; }
        }
    }
}
