using LocalRagAPI.Models;
using System;
using System.Threading.Tasks;

namespace LocalRagAPI.Repositories
{
    public interface IUserRepository
    {
        Task<User> GetByEmailAsync(string email);
        Task CreateAsync(User user);
    }
}
