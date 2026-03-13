using LocalRagAPI.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LocalRagAPI.Repositories
{
    public interface IChatSessionRepository
    {
        Task<ChatSession> CreateAsync(ChatSession session);
        Task<ChatSession> GetByIdAsync(Guid id);
        Task<IEnumerable<ChatSession>> ListByUserAsync(Guid userId);
    }
}
