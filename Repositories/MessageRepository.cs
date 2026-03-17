using LocalRagAPI.Data;
using LocalRagAPI.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LocalRagAPI.Repositories
{
    public class MessageRepository : IMessageRepository
    {
        private readonly ApplicationDbContext _db;

        public MessageRepository(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task AddAsync(Message message)
        {
            _db.Messages.Add(message);
            await _db.SaveChangesAsync();
        }

        public async Task<IEnumerable<Message>> ListBySessionAsync(Guid sessionId)
        {
            return await _db.Messages.AsNoTracking().Where(m => m.SessionId == sessionId).ToListAsync();
        }

        public async Task DeleteBySessionAsync(Guid sessionId)
        {
            var messages = await _db.Messages.Where(m => m.SessionId == sessionId).ToListAsync();
            if (!messages.Any()) return;
            _db.Messages.RemoveRange(messages);
            await _db.SaveChangesAsync();
        }
    }
}
