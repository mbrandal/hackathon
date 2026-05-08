using Microsoft.EntityFrameworkCore;

namespace Hackathon.Data;

public class HackathonDb : DbContext
{
    public HackathonDb(DbContextOptions<HackathonDb> options) : base(options) { }

    public DbSet<Badge> Badges => Set<Badge>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamBadge> TeamBadges => Set<TeamBadge>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TeamBadge>()
            .HasKey(tb => new { tb.TeamId, tb.BadgeId });

        modelBuilder.Entity<TeamBadge>()
            .HasOne(tb => tb.Team)
            .WithMany(t => t.TeamBadges)
            .HasForeignKey(tb => tb.TeamId);

        modelBuilder.Entity<TeamBadge>()
            .HasOne(tb => tb.Badge)
            .WithMany(b => b.TeamBadges)
            .HasForeignKey(tb => tb.BadgeId);

        modelBuilder.Entity<Team>()
            .HasIndex(t => t.Name)
            .IsUnique();

        SeedBadges(modelBuilder);
    }

    private static void SeedBadges(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Badge>().HasData(
            new Badge { Id = 1, Name = "Besetningen", Category = "System", Points = 500 },
            new Badge { Id = 2, Name = "Kartbordet", Category = "System", Points = 500 },
            new Badge { Id = 3, Name = "Innovatøren", Category = "Nytt", Points = 300 },
            new Badge { Id = 4, Name = "Brobyggeren", Category = "Nytt", Points = 300 },
            new Badge { Id = 5, Name = "Automaten", Category = "Nytt", Points = 300 },
            new Badge { Id = 6, Name = "Kompasskurs", Category = "Prosess", Points = 200 },
            new Badge { Id = 7, Name = "Manøverbok", Category = "Prosess", Points = 200 },
            new Badge { Id = 8, Name = "Hovedkurs", Category = "Prosess", Points = 200 },
            new Badge { Id = 9, Name = "Ankerpunkt", Category = "Prosess", Points = 200 },
            new Badge { Id = 10, Name = "Havstrøm", Category = "Teknikk", Points = 200 },
            new Badge { Id = 11, Name = "Styrhus", Category = "Teknikk", Points = 200 },
            new Badge { Id = 12, Name = "Vakttårn", Category = "Teknikk", Points = 200 },
            new Badge { Id = 13, Name = "Sjøkabel", Category = "Teknikk", Points = 200 },
            new Badge { Id = 14, Name = "Konvoi", Category = "Samarbeid", Points = 200 },
            new Badge { Id = 15, Name = "Sjøsetting", Category = "Samarbeid", Points = 200 },
            new Badge { Id = 16, Name = "Kursjustering", Category = "Samarbeid", Points = 200 },
            new Badge { Id = 17, Name = "Loggbok", Category = "Kultur", Points = 200 },
            new Badge { Id = 18, Name = "Morgenmønstring", Category = "Kultur", Points = 200 },
            new Badge { Id = 19, Name = "Fyrlykten", Category = "Gull", Points = 500 },
            new Badge { Id = 20, Name = "Maskinrommet", Category = "Gull", Points = 500 },
            new Badge { Id = 21, Name = "Kompasset", Category = "Gull", Points = 500 },
            new Badge { Id = 22, Name = "Autopiloten", Category = "AI", Points = 200 },
            new Badge { Id = 23, Name = "Skipsuret", Category = "AI", Points = 200 },
            new Badge { Id = 24, Name = "Bunkersen", Category = "AI", Points = 500 }
        );
    }
}
