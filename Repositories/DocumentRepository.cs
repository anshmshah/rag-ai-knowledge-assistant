using LocalRagAPI.Data;
using LocalRagAPI.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LocalRagAPI.Repositories
{
    public class DocumentRepository : IDocumentRepository
    {
        private readonly ApplicationDbContext _db;

        public DocumentRepository(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task CreateAsync(Document doc)
        {
            _db.Add(doc);
            await _db.SaveChangesAsync();
        }

        public async Task<Document> GetByIdAsync(Guid id)
        {
            return await _db.Set<Document>().FirstOrDefaultAsync(d => d.Id == id && d.DeletedAt == null);
        }

        public async Task<Document> GetByFileNameAsync(string filename)
        {
            return await _db.Set<Document>().FirstOrDefaultAsync(d => d.FileName == filename && d.DeletedAt == null);
        }

        public async Task<Document> GetByHashAsync(Guid userId, string hash)
        {
            return await _db.Set<Document>().FirstOrDefaultAsync(d => d.UserId == userId && d.Sha256Hash == hash && d.DeletedAt == null);
        }

        public async Task<IEnumerable<Document>> ListByUserAsync(Guid userId)
        {
            return await _db.Set<Document>()
                .AsNoTracking()
                .Where(d => d.UserId == userId && d.DeletedAt == null)
                .ToListAsync();
        }

        public async Task MarkDeletedAsync(Guid id)
        {
            var d = await _db.Set<Document>().FirstOrDefaultAsync(x => x.Id == id);
            if (d == null) return;
            d.DeletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }
}
