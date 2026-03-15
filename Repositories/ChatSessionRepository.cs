using LocalRagAPI.Data;
using LocalRagAPI.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LocalRagAPI.Repositories
{
    public class ChatSessionRepository : IChatSessionRepository
    {
        private readonly ApplicationDbContext _db;

        public ChatSessionRepository(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<ChatSession> CreateAsync(ChatSession session)
        {
            _db.Add(session);
            await _db.SaveChangesAsync();
            return session;
        }

        public async Task<ChatSession> GetByIdAsync(Guid id)
        {
            return await _db.ChatSessions.FirstOrDefaultAsync(s => s.Id == id && s.DeletedAt == null);
        }

        public async Task<IEnumerable<ChatSession>> ListByUserAsync(Guid userId)
        {
            return await _db.ChatSessions.AsNoTracking().Where(s => s.UserId == userId && s.DeletedAt == null).ToListAsync();
        }


        public async Task UpdateTitleAsync(Guid id, string title)
        {
            var s = await _db.ChatSessions.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null);
            if (s == null) return;
            s.Title = title;
            await _db.SaveChangesAsync();
        }

        public async Task DeleteAsync(Guid id)
        {
            var s = await _db.ChatSessions.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null);
            if (s == null) return;
            s.DeletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }
}
