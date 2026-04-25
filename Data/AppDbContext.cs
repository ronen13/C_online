using Microsoft.EntityFrameworkCore;
using QuizSystem.Models;

namespace QuizSystem.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<QuizSession> QuizSessions => Set<QuizSession>();
    public DbSet<Question> Questions => Set<Question>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();

        modelBuilder.Entity<QuizSession>()
            .HasOne(s => s.User)
            .WithMany(u => u.Sessions)
            .HasForeignKey(s => s.UserId);

        modelBuilder.Entity<Question>()
            .HasOne(q => q.Session)
            .WithMany(s => s.Questions)
            .HasForeignKey(q => q.SessionId);
    }
}
