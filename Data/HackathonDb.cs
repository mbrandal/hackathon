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
            new Badge
            {
                Id = 1, Name = "Besetningen", Category = "System", Points = 500, Icon = "\U0001F465",
                Subtitle = "Erstatter: HumaHR",
                Description = "Lag et internt HR-verkt\u00f8y som h\u00e5ndterer frav\u00e6r, medarbeiderdata og godkjenningsflyter. Fokus p\u00e5 det vi faktisk trenger \u2014 ikke 100 funksjoner vi aldri bruker.",
                Criteria = "[\"H\u00e5ndtere frav\u00e6r: ferie, syk, omsorgspermisjon\",\"Medarbeideroversikt med n\u00f8kkeldata\",\"S\u00f8knad med godkjenningsflyt\",\"Teamkalender med tydelig oversikt\"]",
                BonusText = "\u2605 BONUS: +200p for automatiserte varsler ved frav\u00e6r"
            },
            new Badge
            {
                Id = 2, Name = "Kartbordet", Category = "System", Points = 500, Icon = "\U0001F4CA",
                Subtitle = "Erstatter: Float",
                Description = "Bygg en visuell ressursplanlegger der vi ser hvem som jobber med hva og n\u00e5r. M\u00e5let er \u00e5 gi prosjektledere rask oversikt og mulighet til \u00e5 planlegge smart.",
                Criteria = "[\"Visuell planlegging: hvem gj\u00f8r hva, n\u00e5r\",\"Dra-og-slipp skjema\",\"Kapasitetsoversikt per person og team\",\"Filter og visninger tilpasset behov\"]",
                BonusText = "\u2605 BONUS: +200p for kobling til timelistedata"
            },
            new Badge
            {
                Id = 3, Name = "Innovat\u00f8ren", Category = "Nytt", Points = 300, Icon = "\U0001F9E9",
                Subtitle = "Bygg noe helt nytt",
                Description = "Lag et internt verkt\u00f8y som l\u00f8ser et problem vi ikke har system for i dag. Tenk fritt \u2014 hva mangler vi? Hva tar tid manuelt? Hva hadde gjort hverdagen bedre?",
                Criteria = "[\"Tydelig definert problem som verkt\u00f8yet l\u00f8ser\",\"Fungerende prototype (trenger ikke v\u00e6re komplett)\",\"Demo som viser den reelle verdien\",\"Kort pitch: hvem tjener p\u00e5 dette og hvorfor?\"]",
                BonusText = "\u2605 BONUS: +200p hvis verkt\u00f8yet allerede har en ekte bruker etter hackatonet"
            },
            new Badge
            {
                Id = 4, Name = "Brobyggeren", Category = "Nytt", Points = 300, Icon = "\U0001F517",
                Subtitle = "Integrasjon mellom systemer",
                Description = "Koble sammen data som i dag lever i siloer. Bygg en integrasjon mellom to systemer \u2014 eksisterende eller nybyggede under hackatonet. Dataflyten skal v\u00e6re ekte.",
                Criteria = "[\"Reell dataflyt mellom minst to systemer\",\"Visuell demo av integrasjonen i aksjon\",\"Dokumentasjon av API, webhook eller mekanisme\",\"Tydelig nytte: hva muliggj\u00f8r koblingen?\"]",
                BonusText = "\u2605 BONUS: +200p hvis tre eller flere systemer kobles sammen"
            },
            new Badge { Id = 5, Name = "Automaten", Category = "Nytt", Points = 300, Icon = "\u2699\uFE0F" },
            new Badge { Id = 6, Name = "Kompasskurs", Category = "Prosess", Points = 200, Icon = "\U0001F9ED" },
            new Badge { Id = 7, Name = "Man\u00f8verbok", Category = "Prosess", Points = 200, Icon = "\U0001F4D6" },
            new Badge { Id = 8, Name = "Hovedkurs", Category = "Prosess", Points = 200, Icon = "\U0001F3AF" },
            new Badge { Id = 9, Name = "Ankerpunkt", Category = "Prosess", Points = 200, Icon = "\u2693" },
            new Badge { Id = 10, Name = "Havstr\u00f8m", Category = "Teknikk", Points = 200, Icon = "\U0001F30A" },
            new Badge { Id = 11, Name = "Styrhus", Category = "Teknikk", Points = 200, Icon = "\U0001F6F0\uFE0F" },
            new Badge { Id = 12, Name = "Vaktt\u00e5rn", Category = "Teknikk", Points = 200, Icon = "\U0001F3F0" },
            new Badge { Id = 13, Name = "Sj\u00f8kabel", Category = "Teknikk", Points = 200, Icon = "\U0001F50C" },
            new Badge { Id = 14, Name = "Konvoi", Category = "Samarbeid", Points = 200, Icon = "\U0001F6A2" },
            new Badge { Id = 15, Name = "Sj\u00f8setting", Category = "Samarbeid", Points = 200, Icon = "\U0001F680" },
            new Badge { Id = 16, Name = "Kursjustering", Category = "Samarbeid", Points = 200, Icon = "\U0001F504" },
            new Badge { Id = 17, Name = "Loggbok", Category = "Kultur", Points = 200, Icon = "\U0001F4DD" },
            new Badge { Id = 18, Name = "Morgenm\u00f8nstring", Category = "Kultur", Points = 200, Icon = "\u2600\uFE0F" },
            new Badge { Id = 19, Name = "Fyrlykten", Category = "Gull", Points = 500, Icon = "\U0001F4A1" },
            new Badge { Id = 20, Name = "Maskinrommet", Category = "Gull", Points = 500, Icon = "\U0001F527" },
            new Badge { Id = 21, Name = "Kompasset", Category = "Gull", Points = 500, Icon = "\U0001F9ED" },
            new Badge { Id = 22, Name = "Autopiloten", Category = "AI", Points = 200, Icon = "\U0001F916" },
            new Badge { Id = 23, Name = "Skipsuret", Category = "AI", Points = 200, Icon = "\u23F0" },
            new Badge { Id = 24, Name = "Bunkersen", Category = "AI", Points = 500, Icon = "\u26FD" }
        );
    }
}
