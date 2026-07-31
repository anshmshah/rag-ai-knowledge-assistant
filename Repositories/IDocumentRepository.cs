using LocalRagAPI.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LocalRagAPI.Repositories
{
    public interface IDocumentRepository
    {
        Task<Document> GetByIdAsync(Guid id);
        Task CreateAsync(Document doc);
        Task<IEnumerable<Document>> ListByUserAsync(Guid userId);
        Task MarkDeletedAsync(Guid id);
        Task<Document> GetByFileNameAsync(string filename);
        Task<Document> GetByHashAsync(Guid userId, string hash);
    }
}
