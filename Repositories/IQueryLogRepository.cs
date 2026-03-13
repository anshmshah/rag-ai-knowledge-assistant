using LocalRagAPI.Models;
using System;
using System.Threading.Tasks;

namespace LocalRagAPI.Repositories
{
    public interface IQueryLogRepository
    {
        Task CreateAsync(QueryLog log);
    }
}
