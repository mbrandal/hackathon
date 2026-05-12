namespace Hackathon.Data;

public class Badge
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public int Points { get; set; }
    public string Icon { get; set; } = "\U0001F3C5";
    public string? Subtitle { get; set; }
    public string? Description { get; set; }
    public string? Criteria { get; set; }
    public string? BonusText { get; set; }
    public List<TeamBadge> TeamBadges { get; set; } = [];
}

public class Team
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Pin { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<TeamBadge> TeamBadges { get; set; } = [];
}

public class TeamBadge
{
    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;
    public int BadgeId { get; set; }
    public Badge Badge { get; set; } = null!;
    public DateTime ClaimedAt { get; set; } = DateTime.UtcNow;
}
