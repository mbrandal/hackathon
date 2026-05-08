using Hackathon.Data;
using Microsoft.EntityFrameworkCore;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<HackathonDb>(options =>
    options.UseSqlite("Data Source=hackathon.db"));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HackathonDb>();
    db.Database.EnsureCreated();
}

var categoryColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["System"] = "#e91e63",
    ["Nytt"] = "#ffc107",
    ["Prosess"] = "#9c27b0",
    ["Teknikk"] = "#2196f3",
    ["Samarbeid"] = "#ff9800",
    ["Kultur"] = "#8bc34a",
    ["Gull"] = "#ffd700",
    ["AI"] = "#00bcd4"
};

// ─── API ───

app.MapPost("/api/teams", async (HackathonDb db, HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var doc = System.Text.Json.JsonDocument.Parse(body);
    var name = doc.RootElement.GetProperty("name").GetString()?.Trim();

    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { error = "Team name is required" });

    if (await db.Teams.AnyAsync(t => t.Name == name))
        return Results.Conflict(new { error = "Team already exists" });

    var team = new Team { Name = name };
    db.Teams.Add(team);
    await db.SaveChangesAsync();
    return Results.Created($"/api/teams/{team.Id}", new { team.Id, team.Name });
});

app.MapPost("/api/teams/{teamId}/badges/{badgeId}", async (int teamId, int badgeId, HackathonDb db) =>
{
    var team = await db.Teams.FindAsync(teamId);
    if (team is null) return Results.NotFound(new { error = "Team not found" });

    var badge = await db.Badges.FindAsync(badgeId);
    if (badge is null) return Results.NotFound(new { error = "Badge not found" });

    var existing = await db.TeamBadges.FindAsync(teamId, badgeId);
    if (existing is not null)
    {
        db.TeamBadges.Remove(existing);
        await db.SaveChangesAsync();
        return Results.Ok(new { claimed = false, badge = badge.Name });
    }

    db.TeamBadges.Add(new TeamBadge { TeamId = teamId, BadgeId = badgeId });
    await db.SaveChangesAsync();
    return Results.Ok(new { claimed = true, badge = badge.Name });
});

app.MapGet("/api/scoreboard", async (HackathonDb db) =>
{
    var teams = await db.Teams
        .Include(t => t.TeamBadges).ThenInclude(tb => tb.Badge)
        .OrderByDescending(t => t.TeamBadges.Sum(tb => tb.Badge.Points))
        .ThenBy(t => t.Name)
        .ToListAsync();

    return teams.Select(t => new
    {
        t.Id, t.Name,
        Score = t.TeamBadges.Sum(tb => tb.Badge.Points),
        BadgeCount = t.TeamBadges.Count,
        Badges = t.TeamBadges.Select(tb => new { tb.Badge.Id, tb.Badge.Name, tb.Badge.Points, tb.Badge.Category })
    });
});

app.MapDelete("/api/teams/{teamId}", async (int teamId, HackathonDb db) =>
{
    var team = await db.Teams.Include(t => t.TeamBadges).FirstOrDefaultAsync(t => t.Id == teamId);
    if (team is null) return Results.NotFound();
    db.Teams.Remove(team);
    await db.SaveChangesAsync();
    return Results.Ok();
});

// ─── HTML PAGES ───

app.MapGet("/", async (HackathonDb db) =>
{
    var teams = await db.Teams
        .Include(t => t.TeamBadges).ThenInclude(tb => tb.Badge)
        .OrderByDescending(t => t.TeamBadges.Sum(tb => tb.Badge.Points))
        .ThenBy(t => t.Name)
        .ToListAsync();

    const int maxScore = 6700;
    var html = new StringBuilder();
    html.Append(PageHead("Hackathon Scoreboard", autoRefresh: 5));
    html.Append("""
    <div class="container">
        <h1>🏆 HACKATHON SCOREBOARD</h1>
        <div class="podium">
    """);

    var podiumEmojis = new[] { "🥇", "🥈", "🥉" };
    var podiumClasses = new[] { "gold", "silver", "bronze" };
    for (int i = 0; i < Math.Min(3, teams.Count); i++)
    {
        var t = teams[i];
        var score = t.TeamBadges.Sum(tb => tb.Badge.Points);
        html.Append($"""
            <div class="podium-card {podiumClasses[i]}">
                <div class="podium-emoji">{podiumEmojis[i]}</div>
                <div class="podium-name">{Esc(t.Name)}</div>
                <div class="podium-score">{score}</div>
                <div class="podium-badges">{t.TeamBadges.Count} badges</div>
                <div class="progress-bar"><div class="progress-fill" style="width:{(maxScore > 0 ? score * 100 / maxScore : 0)}%"></div></div>
            </div>
        """);
    }
    html.Append("</div>");

    if (teams.Count > 3)
    {
        html.Append("""<table class="ranking"><tr><th>#</th><th>Lag</th><th>Poeng</th><th>Badges</th><th>Fremgang</th></tr>""");
        for (int i = 3; i < teams.Count; i++)
        {
            var t = teams[i];
            var score = t.TeamBadges.Sum(tb => tb.Badge.Points);
            html.Append($"""
                <tr>
                    <td>{i + 1}</td>
                    <td>{Esc(t.Name)}</td>
                    <td class="score">{score}</td>
                    <td>{t.TeamBadges.Count}/24</td>
                    <td><div class="progress-bar table-bar"><div class="progress-fill" style="width:{(maxScore > 0 ? score * 100 / maxScore : 0)}%"></div></div></td>
                </tr>
            """);
        }
        html.Append("</table>");
    }

    if (teams.Count == 0)
        html.Append("""<p class="empty">Ingen lag registrert ennå. Gå til <code>/admin</code> for å opprette lag.</p>""");

    html.Append("""
        <div class="footer">
            <a href="/admin">⚙️ Admin</a>
            <span class="max-score">Maks: 6 700 poeng · 24 badges</span>
        </div>
    </div></body></html>
    """);

    return Results.Content(html.ToString(), "text/html");
});

app.MapGet("/team/{teamId:int}", async (int teamId, HackathonDb db) =>
{
    var team = await db.Teams
        .Include(t => t.TeamBadges).ThenInclude(tb => tb.Badge)
        .FirstOrDefaultAsync(t => t.Id == teamId);

    if (team is null)
        return Results.Content(PageHead("Ikke funnet") + "<div class='container'><h1>Lag ikke funnet</h1><a href='/admin'>Tilbake</a></div></body></html>", "text/html");

    var allBadges = await db.Badges.OrderBy(b => b.Category).ThenBy(b => b.Name).ToListAsync();
    var claimedIds = team.TeamBadges.Select(tb => tb.BadgeId).ToHashSet();
    var score = team.TeamBadges.Sum(tb => tb.Badge.Points);

    var html = new StringBuilder();
    html.Append(PageHead($"{team.Name} — Badges"));
    html.Append($"""
    <div class="container">
        <a href="/" class="back-link">← Scoreboard</a>
        <h1>{Esc(team.Name)}</h1>
        <div class="team-score">
            <span class="score-number" id="score">{score}</span>
            <span class="score-label"> / 6 700 poeng</span>
            <span class="badge-count" id="badgeCount">{claimedIds.Count} / 24 badges</span>
        </div>
        <div class="badge-grid">
    """);

    var grouped = allBadges.GroupBy(b => b.Category);
    foreach (var group in grouped)
    {
        var color = categoryColors.GetValueOrDefault(group.Key, "#666");
        html.Append($"""<div class="category-group"><h2 class="category-title" style="border-left:4px solid {color};padding-left:12px">{Esc(group.Key)}</h2><div class="badges">""");
        foreach (var badge in group)
        {
            var claimed = claimedIds.Contains(badge.Id);
            html.Append($"""
                <button class="badge-btn {(claimed ? "claimed" : "")}"
                        style="--cat-color:{color}"
                        onclick="toggle({team.Id},{badge.Id},this)"
                        data-points="{badge.Points}">
                    <span class="badge-check">{(claimed ? "✅" : "⬜")}</span>
                    <span class="badge-name">{Esc(badge.Name)}</span>
                    <span class="badge-points">{badge.Points}p</span>
                </button>
            """);
        }
        html.Append("</div></div>");
    }

    html.Append("""
        </div>
    </div>
    <script>
    async function toggle(tid,bid,btn){
        btn.disabled=true;
        try{
            const r=await fetch(`/api/teams/${tid}/badges/${bid}`,{method:'POST'});
            const d=await r.json();
            const c=btn.querySelector('.badge-check');
            const p=parseInt(btn.dataset.points);
            const s=document.getElementById('score');
            const n=document.getElementById('badgeCount');
            let sc=parseInt(s.textContent),nc=parseInt(n.textContent);
            if(d.claimed){btn.classList.add('claimed');c.textContent='✅';sc+=p;nc++;}
            else{btn.classList.remove('claimed');c.textContent='⬜';sc-=p;nc--;}
            s.textContent=sc;n.textContent=nc+' / 24 badges';
        }finally{btn.disabled=false;}
    }
    </script></body></html>
    """);

    return Results.Content(html.ToString(), "text/html");
});

app.MapGet("/admin", async (HackathonDb db) =>
{
    var teams = await db.Teams.Include(t => t.TeamBadges).ThenInclude(tb => tb.Badge).OrderBy(t => t.Name).ToListAsync();
    var html = new StringBuilder();
    html.Append(PageHead("Admin"));
    html.Append("""
    <div class="container">
        <a href="/" class="back-link">← Scoreboard</a>
        <h1>⚙️ Admin — Lag</h1>
        <div class="admin-form">
            <input type="text" id="tn" placeholder="Lagnavn..." maxlength="50" />
            <button onclick="go()">Opprett lag</button>
        </div>
        <div id="msg"></div>
        <div class="team-list">
    """);

    foreach (var t in teams)
    {
        html.Append($"""
            <div class="team-card" id="t-{t.Id}">
                <a href="/team/{t.Id}" class="team-link">{Esc(t.Name)}</a>
                <span class="team-meta">{t.TeamBadges.Sum(tb => tb.Badge.Points)} poeng</span>
                <button class="delete-btn" onclick="del({t.Id})">🗑️</button>
            </div>
        """);
    }

    if (teams.Count == 0)
        html.Append("""<p class="empty">Ingen lag ennå.</p>""");

    html.Append("""
        </div>
    </div>
    <script>
    async function go(){
        const i=document.getElementById('tn'),n=i.value.trim();
        if(!n)return;
        const r=await fetch('/api/teams',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:n})});
        const m=document.getElementById('msg');
        if(r.ok){m.innerHTML='<span style="color:#4caf50">✅ Opprettet!</span>';i.value='';setTimeout(()=>location.reload(),500);}
        else{const e=await r.json();m.innerHTML=`<span style="color:#f44336">❌ ${e.error}</span>`;}
    }
    document.getElementById('tn').addEventListener('keydown',e=>{if(e.key==='Enter')go();});
    async function del(id){if(!confirm('Slett?'))return;await fetch(`/api/teams/${id}`,{method:'DELETE'});document.getElementById(`t-${id}`)?.remove();}
    </script></body></html>
    """);

    return Results.Content(html.ToString(), "text/html");
});

app.Run();

static string Esc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

static string PageHead(string title, int autoRefresh = 0)
{
    var refresh = autoRefresh > 0 ? $"""<meta http-equiv="refresh" content="{autoRefresh}">""" : "";
    return $$$"""
    <!DOCTYPE html><html lang="no"><head>
    <meta charset="utf-8"/><meta name="viewport" content="width=device-width,initial-scale=1"/>
    {{{refresh}}}
    <title>{{{title}}}</title>
    <style>
    *{margin:0;padding:0;box-sizing:border-box}
    body{background:#0d1117;color:#e6edf3;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif;min-height:100vh}
    .container{max-width:1100px;margin:0 auto;padding:24px 16px}
    h1{text-align:center;font-size:2.5rem;margin-bottom:32px;background:linear-gradient(135deg,#ffd700,#ff9800);-webkit-background-clip:text;-webkit-text-fill-color:transparent;background-clip:text}
    .back-link{color:#58a6ff;text-decoration:none;display:inline-block;margin-bottom:16px;font-size:.9rem}
    .back-link:hover{text-decoration:underline}
    .podium{display:flex;gap:20px;justify-content:center;margin-bottom:40px;flex-wrap:wrap}
    .podium-card{background:#161b22;border-radius:16px;padding:32px 28px;text-align:center;min-width:240px;flex:1;max-width:320px;border:2px solid #30363d;transition:transform .2s}
    .podium-card:hover{transform:translateY(-4px)}
    .podium-card.gold{border-color:#ffd700;box-shadow:0 0 30px rgba(255,215,0,.15)}
    .podium-card.silver{border-color:#c0c0c0;box-shadow:0 0 20px rgba(192,192,192,.1)}
    .podium-card.bronze{border-color:#cd7f32;box-shadow:0 0 20px rgba(205,127,50,.1)}
    .podium-emoji{font-size:3rem;margin-bottom:8px}
    .podium-name{font-size:1.5rem;font-weight:700;margin-bottom:4px}
    .podium-score{font-size:2.5rem;font-weight:800;color:#ffd700}
    .podium-badges{color:#8b949e;margin-top:4px;margin-bottom:12px}
    .progress-bar{background:#21262d;border-radius:8px;height:10px;overflow:hidden}
    .progress-fill{height:100%;background:linear-gradient(90deg,#ffd700,#ff9800);border-radius:8px;transition:width .5s}
    .table-bar{height:8px;min-width:120px}
    .ranking{width:100%;border-collapse:collapse;margin-top:8px}
    .ranking th{padding:12px 16px;text-align:left;border-bottom:2px solid #30363d;color:#8b949e;font-size:.85rem;text-transform:uppercase}
    .ranking td{padding:14px 16px;border-bottom:1px solid #21262d}
    .ranking tr:hover{background:#161b22}
    .ranking .score{font-weight:700;color:#ffd700;font-size:1.2rem}
    .team-score{text-align:center;margin-bottom:32px;background:#161b22;border-radius:12px;padding:24px}
    .score-number{font-size:3rem;font-weight:800;color:#ffd700}
    .score-label{font-size:1.2rem;color:#8b949e}
    .badge-count{display:block;color:#8b949e;margin-top:4px}
    .category-group{margin-bottom:24px}
    .category-title{font-size:1.1rem;margin-bottom:12px;color:#e6edf3}
    .badges{display:grid;grid-template-columns:repeat(auto-fill,minmax(200px,1fr));gap:8px}
    .badge-btn{display:flex;align-items:center;gap:8px;padding:12px 16px;background:#161b22;border:2px solid #30363d;border-radius:10px;color:#e6edf3;cursor:pointer;font-size:.95rem;transition:all .15s;text-align:left;width:100%}
    .badge-btn:hover{border-color:var(--cat-color);background:#1c2129}
    .badge-btn.claimed{border-color:var(--cat-color);background:color-mix(in srgb,var(--cat-color) 12%,#161b22)}
    .badge-check{font-size:1.1rem;flex-shrink:0}
    .badge-name{flex:1;font-weight:500}
    .badge-points{font-weight:700;color:var(--cat-color);font-size:.85rem}
    .admin-form{display:flex;gap:12px;margin-bottom:20px}
    .admin-form input{flex:1;padding:12px 16px;background:#161b22;border:2px solid #30363d;border-radius:8px;color:#e6edf3;font-size:1rem}
    .admin-form input:focus{outline:none;border-color:#58a6ff}
    .admin-form button{padding:12px 24px;background:#238636;border:none;border-radius:8px;color:#fff;font-size:1rem;font-weight:600;cursor:pointer}
    .admin-form button:hover{background:#2ea043}
    #msg{margin-bottom:16px;font-size:.95rem}
    .team-list{display:flex;flex-direction:column;gap:8px}
    .team-card{display:flex;align-items:center;gap:12px;padding:14px 18px;background:#161b22;border-radius:10px;border:1px solid #30363d}
    .team-link{flex:1;color:#58a6ff;text-decoration:none;font-weight:600;font-size:1.1rem}
    .team-link:hover{text-decoration:underline}
    .team-meta{color:#8b949e}
    .delete-btn{background:none;border:none;cursor:pointer;font-size:1.2rem;opacity:.5}
    .delete-btn:hover{opacity:1}
    .empty{text-align:center;color:#8b949e;padding:40px}
    .empty code{background:#161b22;padding:2px 8px;border-radius:4px}
    .footer{display:flex;justify-content:space-between;align-items:center;margin-top:40px;padding-top:20px;border-top:1px solid #21262d;color:#8b949e;font-size:.85rem}
    .footer a{color:#58a6ff;text-decoration:none}
    .footer a:hover{text-decoration:underline}
    @media(max-width:700px){h1{font-size:1.6rem}.podium{flex-direction:column;align-items:center}.podium-card{min-width:unset;width:100%}.badges{grid-template-columns:1fr}.admin-form{flex-direction:column}}
    </style></head><body>
    """;
}
