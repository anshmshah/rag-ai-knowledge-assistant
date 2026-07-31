using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace LocalRagAPI.Services
{
    public class FileHashService
    {
        public async Task<string> ComputeSha256Async(Stream stream)
        {
            if (stream == null || !stream.CanRead)
            {
                throw new ArgumentException("Stream is null or unreadable.");
            }

            var originalPosition = stream.Position;
            try
            {
                using var sha256 = SHA256.Create();
                var hashBytes = await sha256.ComputeHashAsync(stream);
                return Convert.ToHexString(hashBytes).ToLowerInvariant();
            }
            finally
            {
                if (stream.CanSeek)
                {
                    stream.Position = originalPosition;
                }
            }
        }
    }
}
