using LocalRagAPI.Data;
using LocalRagAPI.Models;
using System.Threading.Tasks;

namespace LocalRagAPI.Repositories
{
    public class QueryLogRepository : IQueryLogRepository
    {
        private readonly ApplicationDbContext _db;

        public QueryLogRepository(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task CreateAsync(QueryLog log)
        {
            _db.QueryLogs.Add(log);
            await _db.SaveChangesAsync();
        }
    }
}
