using LocalRagAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace LocalRagAPI.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        {
        }

        public DbSet<User> Users { get; set; }
        public DbSet<Document> Documents { get; set; }
        public DbSet<ChatSession> ChatSessions { get; set; }
        public DbSet<Message> Messages { get; set; }
        public DbSet<QueryLog> QueryLogs { get; set; }
        // Future entities: Documents, ChatSessions, Messages, QueryLogs

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<User>(b =>
            {
                b.HasKey(u => u.Id);
                b.HasIndex(u => u.Email).IsUnique();
                b.Property(u => u.Email).IsRequired();
                b.Property(u => u.PasswordHash).IsRequired();
            });

            modelBuilder.Entity<Document>(b =>
            {
                b.HasKey(d => d.Id);
                b.Property(d => d.FileName).IsRequired();
                b.Property(d => d.UserId).IsRequired();
                b.Property(d => d.FilePath).IsRequired();
                
                b.HasIndex(d => new { d.UserId, d.Sha256Hash })
                 .IsUnique()
                 .HasFilter("\"DeletedAt\" IS NULL");
            });

            modelBuilder.Entity<ChatSession>(b =>
            {
                b.HasKey(s => s.Id);
                b.Property(s => s.UserId).IsRequired();
                b.Property(s => s.Title).IsRequired(false);
                b.Property(s => s.DeletedAt).IsRequired(false);
            });

            modelBuilder.Entity<Message>(b =>
            {
                b.HasKey(m => m.Id);
                b.Property(m => m.SessionId).IsRequired();
                b.Property(m => m.Role).IsRequired();
                b.Property(m => m.Content).IsRequired();
            });

            modelBuilder.Entity<QueryLog>(b =>
            {
                b.HasKey(q => q.Id);
                b.Property(q => q.UserId).IsRequired();
                b.Property(q => q.Question).IsRequired();
                b.Property(q => q.Answer).IsRequired(false);
            });
        }
    }
}
