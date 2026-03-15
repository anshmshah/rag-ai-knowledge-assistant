using LocalRagAPI.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LocalRagAPI.Repositories
{
    public interface IMessageRepository
    {
        Task AddAsync(Message message);
        Task<IEnumerable<Message>> ListBySessionAsync(Guid sessionId);
        Task DeleteBySessionAsync(Guid sessionId);
    }
}
