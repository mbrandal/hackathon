using Hackathon.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<HackathonDb>(options =>
    options.UseSqlite("Data Source=hackathon.db"));
builder.Services.AddDataProtection();
builder.Services.AddHostedService<SnapshotService>();

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

const string AdminPin = "hack2026";
const string CookieName = "hackathon_team";
var HackathonEnd = new DateTime(2026, 5, 15, 16, 0, 0, DateTimeKind.Local);

int? GetTeamId(HttpContext ctx)
{
    var cookie = ctx.Request.Cookies[CookieName];
    if (cookie is null) return null;
    try
    {
        var dp = ctx.RequestServices.GetRequiredService<IDataProtectionProvider>();
        return int.Parse(dp.CreateProtector("hackathon").Unprotect(cookie));
    }
    catch { return null; }
}

bool IsAdmin(HttpContext ctx)
{
    var cookie = ctx.Request.Cookies["hackathon_admin"];
    if (cookie is null) return false;
    try
    {
        var dp = ctx.RequestServices.GetRequiredService<IDataProtectionProvider>();
        return dp.CreateProtector("hackathon-admin").Unprotect(cookie) == "admin";
    }
    catch { return false; }
}

string GeneratePin() => Random.Shared.Next(100000, 999999).ToString();

// ─── API ───

app.MapPost("/api/teams", async (HackathonDb db, HttpContext ctx) =>
{
    if (!IsAdmin(ctx))
        return Results.Json(new { error = "Admin-tilgang kreves" }, statusCode: 403);

    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var doc = JsonDocument.Parse(body);
    var name = doc.RootElement.GetProperty("name").GetString()?.Trim();
    var emoji = doc.RootElement.TryGetProperty("emoji", out var eProp) ? eProp.GetString() ?? "\U0001F680" : "\U0001F680";

    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { error = "Team name is required" });

    if (await db.Teams.AnyAsync(t => t.Name == name))
        return Results.Conflict(new { error = "Team already exists" });

    var team = new Team { Name = name, Pin = GeneratePin(), Emoji = emoji };
    db.Teams.Add(team);
    await db.SaveChangesAsync();
    return Results.Created($"/api/teams/{team.Id}", new { team.Id, team.Name, team.Pin, team.Emoji });
});

app.MapPost("/api/teams/{teamId}/badges/{badgeId}", async (int teamId, int badgeId, HackathonDb db, HttpContext ctx) =>
{
    var loggedInTeamId = GetTeamId(ctx);
    if (loggedInTeamId != teamId && !IsAdmin(ctx))
        return Results.Json(new { error = "Du kan bare endre badges for ditt eget lag" }, statusCode: 403);

    var team = await db.Teams.FindAsync(teamId);
    if (team is null) return Results.NotFound(new { error = "Team not found" });

    var badge = await db.Badges.FindAsync(badgeId);
    if (badge is null) return Results.NotFound(new { error = "Badge not found" });

    var existing = await db.TeamBadges.FindAsync(teamId, badgeId);
    if (existing is not null)
    {
        db.TeamBadges.Remove(existing);
        db.ActivityLogs.Add(new ActivityLog { TeamId = teamId, TeamName = team.Name, TeamEmoji = team.Emoji, BadgeId = badgeId, BadgeName = badge.Name, Points = badge.Points, Claimed = false });
        await db.SaveChangesAsync();
        return Results.Ok(new { claimed = false, badge = badge.Name, points = badge.Points });
    }

    db.TeamBadges.Add(new TeamBadge { TeamId = teamId, BadgeId = badgeId });
    db.ActivityLogs.Add(new ActivityLog { TeamId = teamId, TeamName = team.Name, TeamEmoji = team.Emoji, BadgeId = badgeId, BadgeName = badge.Name, Points = badge.Points, Claimed = true });
    await db.SaveChangesAsync();
    return Results.Ok(new { claimed = true, badge = badge.Name, points = badge.Points });
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
        t.Id, t.Name, t.Emoji,
        Score = t.TeamBadges.Sum(tb => tb.Badge.Points),
        BadgeCount = t.TeamBadges.Count,
        Badges = t.TeamBadges.Select(tb => new { tb.Badge.Id, tb.Badge.Name, tb.Badge.Points, tb.Badge.Category })
    });
});

app.MapGet("/api/activity", async (HackathonDb db, int? limit, long? since) =>
{
    var query = db.ActivityLogs.OrderByDescending(a => a.Timestamp).AsQueryable();
    if (since.HasValue)
    {
        var sinceTime = DateTimeOffset.FromUnixTimeMilliseconds(since.Value).UtcDateTime;
        query = db.ActivityLogs.Where(a => a.Timestamp > sinceTime).OrderByDescending(a => a.Timestamp);
    }
    return await query.Take(limit ?? 20).Select(a => new
    {
        a.Id, a.TeamName, a.TeamEmoji, a.BadgeName, a.Points, a.Claimed,
        Timestamp = a.Timestamp.ToString("HH:mm"),
        UnixMs = new DateTimeOffset(a.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds()
    }).ToListAsync();
});

app.MapGet("/api/history", async (HackathonDb db) =>
{
    var snapshots = await db.ScoreSnapshots.OrderBy(s => s.Timestamp).ToListAsync();
    var grouped = snapshots.GroupBy(s => s.TeamId).Select(g => new
    {
        TeamId = g.Key,
        TeamName = g.First().TeamName,
        Data = g.Select(s => new { t = new DateTimeOffset(s.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(), s.Score })
    });
    return grouped;
});

app.MapDelete("/api/teams/{teamId}", async (int teamId, HackathonDb db, HttpContext ctx) =>
{
    if (!IsAdmin(ctx))
        return Results.Json(new { error = "Admin-tilgang kreves" }, statusCode: 403);

    var team = await db.Teams.Include(t => t.TeamBadges).FirstOrDefaultAsync(t => t.Id == teamId);
    if (team is null) return Results.NotFound();
    db.Teams.Remove(team);
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapPost("/api/login", async (HackathonDb db, HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var doc = JsonDocument.Parse(body);
    var name = doc.RootElement.GetProperty("name").GetString()?.Trim() ?? "";
    var pin = doc.RootElement.GetProperty("pin").GetString()?.Trim() ?? "";

    var team = await db.Teams.FirstOrDefaultAsync(t => t.Name == name && t.Pin == pin);
    if (team is null)
        return Results.Json(new { error = "Feil lagnavn eller PIN" }, statusCode: 401);

    var dp = ctx.RequestServices.GetRequiredService<IDataProtectionProvider>();
    var protector = dp.CreateProtector("hackathon");
    ctx.Response.Cookies.Append(CookieName, protector.Protect(team.Id.ToString()),
        new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Expires = DateTimeOffset.UtcNow.AddDays(7) });

    return Results.Ok(new { team.Id, team.Name });
});

app.MapPost("/api/admin-login", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var doc = JsonDocument.Parse(body);
    var pin = doc.RootElement.GetProperty("pin").GetString()?.Trim() ?? "";

    if (pin != AdminPin)
        return Results.Json(new { error = "Feil admin-PIN" }, statusCode: 401);

    var dp = ctx.RequestServices.GetRequiredService<IDataProtectionProvider>();
    var protector = dp.CreateProtector("hackathon-admin");
    ctx.Response.Cookies.Append("hackathon_admin", protector.Protect("admin"),
        new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Expires = DateTimeOffset.UtcNow.AddDays(7) });

    return Results.Ok(new { ok = true });
});

app.MapPost("/api/logout", (HttpContext ctx) =>
{
    ctx.Response.Cookies.Delete(CookieName);
    ctx.Response.Cookies.Delete("hackathon_admin");
    return Results.Ok();
});

// ─── HTML PAGES ───

app.MapGet("/", async (HackathonDb db, HttpContext ctx) =>
{
    var teams = await db.Teams
        .Include(t => t.TeamBadges).ThenInclude(tb => tb.Badge)
        .OrderByDescending(t => t.TeamBadges.Sum(tb => tb.Badge.Points))
        .ThenBy(t => t.Name)
        .ToListAsync();

    var recentActivity = await db.ActivityLogs.OrderByDescending(a => a.Timestamp).Take(10).ToListAsync();

    const int maxScore = 6700;
    var html = new StringBuilder();
    html.Append(PageHead("Hackathon Scoreboard", ctx));
    html.Append("""
    <div class="container">
        <h1>🏆 HACKATHON SCOREBOARD</h1>
    """);

    // Podium (silver-gold-bronze layout)
    if (teams.Count >= 1)
    {
        html.Append("""<div class="podium-wrapper">""");
        var podiumOrder = new[] { 1, 0, 2 }; // silver, gold, bronze
        var podiumClasses = new[] { "gold", "silver", "bronze" };
        var podiumLabels = new[] { "1", "2", "3" };
        for (int pi = 0; pi < Math.Min(3, teams.Count); pi++)
        {
            var idx = podiumOrder[pi];
            if (idx >= teams.Count) continue;
            var t = teams[idx];
            var score = t.TeamBadges.Sum(tb => tb.Badge.Points);
            var pct = maxScore > 0 ? score * 100 / maxScore : 0;
            html.Append($"""
                <div class="podium-card {podiumClasses[idx]}" style="order:{pi}">
                    <div class="podium-rank">{podiumLabels[idx]}</div>
                    <div class="podium-emoji">{t.Emoji}</div>
                    <div class="podium-name">{Esc(t.Name)}</div>
                    <div class="podium-score">{score}</div>
                    <div class="podium-badges">{t.TeamBadges.Count}/24 badges</div>
                    <svg class="progress-ring" viewBox="0 0 64 64"><circle class="ring-bg" cx="32" cy="32" r="28"/><circle class="ring-fill" cx="32" cy="32" r="28" style="stroke-dashoffset:{176 - (pct * 176 / 100)}"/></svg>
                </div>
            """);
        }
        html.Append("</div>");
    }

    // Expand-button for full ranking
    if (teams.Count > 3)
    {
        html.Append("""<div class="expand-section"><button class="expand-btn" onclick="document.getElementById('full-ranking').classList.toggle('hidden');this.textContent=this.textContent.includes('Se')? '▲ Skjul scoreboard':'▼ Se hele scoreboard'">▼ Se hele scoreboard</button></div>""");
        html.Append("""<div id="full-ranking" class="hidden">""");
        html.Append("""<table class="ranking"><tr><th>#</th><th>Lag</th><th>Poeng</th><th>Badges</th><th>Fremgang</th></tr>""");
        for (int i = 0; i < teams.Count; i++)
        {
            var t = teams[i];
            var score = t.TeamBadges.Sum(tb => tb.Badge.Points);
            html.Append($"""
                <tr>
                    <td>{i + 1}</td>
                    <td>{t.Emoji} {Esc(t.Name)}</td>
                    <td class="score">{score}</td>
                    <td>{t.TeamBadges.Count}/24</td>
                    <td><div class="progress-bar table-bar"><div class="progress-fill" style="width:{(maxScore > 0 ? score * 100 / maxScore : 0)}%"></div></div></td>
                </tr>
            """);
        }
        html.Append("</table></div>");
    }

    if (teams.Count == 0)
        html.Append("""<p class="empty">Ingen lag registrert ennå.</p>""");

    // Activity feed
    html.Append("""<div class="activity-feed"><h3>📡 Live Activity</h3><div id="feed-list">""");
    foreach (var a in recentActivity)
    {
        var verb = a.Claimed ? "fikk" : "mistet";
        var sign = a.Claimed ? "+" : "-";
        html.Append($"""<div class="feed-item {(a.Claimed ? "claimed" : "unclaimed")}"><span class="feed-time">{a.Timestamp:HH:mm}</span> {a.TeamEmoji} <strong>{Esc(a.TeamName)}</strong> {verb} <em>{Esc(a.BadgeName)}</em> ({sign}{a.Points}p)</div>""");
    }
    if (recentActivity.Count == 0)
        html.Append("""<div class="feed-empty">Ingen aktivitet ennå...</div>""");
    html.Append("</div></div>");

    html.Append("""
    </div>
    <script>
    let lastEventId=0,soundEnabled=false;
    const feedEl=document.getElementById('feed-list');
    async function pollActivity(){
        try{
            const r=await fetch(`/api/activity?limit=10&since=${lastEventId}`);
            const events=await r.json();
            if(events.length>0){
                lastEventId=events[0].unixMs;
                events.reverse().forEach(e=>{
                    const div=document.createElement('div');
                    div.className='feed-item '+(e.claimed?'claimed':'unclaimed')+' feed-new';
                    div.innerHTML=`<span class="feed-time">${e.timestamp}</span> ${e.teamEmoji} <strong>${e.teamName}</strong> ${e.claimed?'fikk':'mistet'} <em>${e.badgeName}</em> (${e.claimed?'+':'-'}${e.points}p)`;
                    feedEl.prepend(div);
                    if(feedEl.children.length>15)feedEl.lastChild.remove();
                    if(soundEnabled&&e.claimed)playSound(e.points);
                });
                location.reload();
            }
        }catch{}
    }
    setInterval(pollActivity,5000);
    function playSound(pts){
        const ctx=new(window.AudioContext||window.webkitAudioContext)();
        const o=ctx.createOscillator(),g=ctx.createGain();
        o.connect(g);g.connect(ctx.destination);
        o.type='sine';o.frequency.value=pts>=500?880:pts>=300?660:440;
        g.gain.setValueAtTime(0.3,ctx.currentTime);
        g.gain.exponentialRampToValueAtTime(0.01,ctx.currentTime+0.5);
        o.start();o.stop(ctx.currentTime+0.5);
    }
    document.getElementById('sound-toggle')?.addEventListener('click',()=>{soundEnabled=!soundEnabled;document.getElementById('sound-toggle').textContent=soundEnabled?'🔊':'🔇';});
    </script></body></html>
    """);

    return Results.Content(html.ToString(), "text/html");
});

// ─── BADGES GALLERY ───

app.MapGet("/badges", async (HackathonDb db, HttpContext ctx) =>
{
    var badges = await db.Badges.OrderBy(b => b.Category).ThenBy(b => b.Name).ToListAsync();
    var html = new StringBuilder();
    html.Append(PageHead("Alle Badges", ctx));
    html.Append("""
    <div class="container">
        <h1>🏅 ALLE BADGES</h1>
        <p class="subtitle">Klikk på en badge for å lese mer om kriteriene</p>
        <div class="badge-gallery">
    """);

    var grouped = badges.GroupBy(b => b.Category);
    foreach (var group in grouped)
    {
        var color = categoryColors.GetValueOrDefault(group.Key, "#666");
        html.Append($"""<div class="gallery-category"><h2 class="category-title" style="border-left:4px solid {color};padding-left:12px">{Esc(group.Key)}</h2><div class="gallery-grid">""");
        foreach (var b in group)
        {
            html.Append($"""
                <a href="/badges/{b.Id}" class="gallery-card" style="--cat-color:{color}">
                    <div class="badge-circle" style="border-color:{color}">
                        <span class="badge-icon">{b.Icon}</span>
                    </div>
                    <div class="gallery-name">{Esc(b.Name)}</div>
                    <div class="gallery-points" style="color:{color}">{b.Points} POENG</div>
                </a>
            """);
        }
        html.Append("</div></div>");
    }

    html.Append("</div></div></body></html>");
    return Results.Content(html.ToString(), "text/html");
});

// ─── BADGE DETAIL ───

app.MapGet("/badges/{badgeId:int}", async (int badgeId, HackathonDb db, HttpContext ctx) =>
{
    var badge = await db.Badges.FindAsync(badgeId);
    if (badge is null)
        return Results.Content(PageHead("Ikke funnet", ctx) + "<div class='container'><h1>Badge ikke funnet</h1></div></body></html>", "text/html");

    var color = categoryColors.GetValueOrDefault(badge.Category, "#666");
    var html = new StringBuilder();
    html.Append(PageHead(badge.Name, ctx));
    html.Append($"""
    <div class="container">
        <a href="/badges" class="back-link">← Alle badges</a>
        <div class="detail-layout">
            <div class="detail-left">
                <div class="badge-circle-lg" style="border-color:{color}">
                    <span class="badge-icon-lg">{badge.Icon}</span>
                </div>
                <h1 class="detail-name" style="color:{color}">{Esc(badge.Name)}</h1>
                <div class="detail-points-box" style="border-color:{color};color:{color}">{badge.Points} POENG</div>
            </div>
            <div class="detail-right">
    """);

    if (badge.Subtitle is not null)
        html.Append($"""<p class="detail-subtitle"><em>{Esc(badge.Subtitle)}</em></p>""");

    if (badge.Description is not null)
    {
        html.Append($"""
            <div class="detail-section">
                <h3 class="section-header" style="color:{color}">Beskrivelse</h3>
                <p>{Esc(badge.Description)}</p>
            </div>
        """);
    }

    if (badge.Criteria is not null)
    {
        var criteria = JsonSerializer.Deserialize<string[]>(badge.Criteria) ?? [];
        html.Append($"""<div class="detail-section"><h3 class="section-header" style="color:{color}">Kriterier</h3><ul class="criteria-list">""");
        foreach (var c in criteria)
            html.Append($"""<li>→ {Esc(c)}</li>""");
        html.Append("</ul></div>");
    }

    if (badge.BonusText is not null)
        html.Append($"""<div class="bonus-box">{Esc(badge.BonusText)}</div>""");

    if (badge.Description is null && badge.Criteria is null)
    {
        html.Append("""
            <div class="detail-section placeholder-section">
                <p>Detaljer for denne badgen kommer snart...</p>
                <p class="placeholder-hint">Sjekk tilbake her eller spør arrangøren for kriterier.</p>
            </div>
        """);
    }

    html.Append("</div></div></div></body></html>");
    return Results.Content(html.ToString(), "text/html");
});

// ─── TEAM PAGE (auth required) ───

app.MapGet("/team/{teamId:int}", async (int teamId, HackathonDb db, HttpContext ctx) =>
{
    var loggedIn = GetTeamId(ctx);
    var admin = IsAdmin(ctx);
    if (loggedIn != teamId && !admin)
        return Results.Redirect("/login");

    var team = await db.Teams
        .Include(t => t.TeamBadges).ThenInclude(tb => tb.Badge)
        .FirstOrDefaultAsync(t => t.Id == teamId);

    if (team is null)
        return Results.Content(PageHead("Ikke funnet", ctx) + "<div class='container'><h1>Lag ikke funnet</h1></div></body></html>", "text/html");

    var allBadges = await db.Badges.OrderBy(b => b.Category).ThenBy(b => b.Name).ToListAsync();
    var claimedIds = team.TeamBadges.Select(tb => tb.BadgeId).ToHashSet();
    var score = team.TeamBadges.Sum(tb => tb.Badge.Points);

    var html = new StringBuilder();
    html.Append(PageHead($"{team.Name} — Badges", ctx));
    html.Append($"""
    <div class="container">
        <a href="/" class="back-link">← Scoreboard</a>
        <h1>{team.Emoji} {Esc(team.Name)}</h1>
        <div class="team-score">
            <span class="score-number" id="score">{score}</span>
            <span class="score-label"> / 6 700 poeng</span>
            <span class="badge-count" id="badgeCount">{claimedIds.Count} / 24 badges</span>
        </div>
        <div class="badge-grid">
    """);

    var grouped2 = allBadges.GroupBy(b => b.Category);
    foreach (var group in grouped2)
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
    <script src="https://cdn.jsdelivr.net/npm/canvas-confetti@1/dist/confetti.browser.min.js"></script>
    <script>
    async function toggle(tid,bid,btn){
        btn.disabled=true;
        try{
            const r=await fetch(`/api/teams/${tid}/badges/${bid}`,{method:'POST'});
            if(r.status===403){alert('Du har ikke tilgang');return;}
            const d=await r.json();
            const c=btn.querySelector('.badge-check');
            const p=parseInt(btn.dataset.points);
            const s=document.getElementById('score');
            const n=document.getElementById('badgeCount');
            let sc=parseInt(s.textContent),nc=parseInt(n.textContent);
            if(d.claimed){
                btn.classList.add('claimed');c.textContent='✅';sc+=p;nc++;
                confetti({particleCount:100,spread:70,origin:{y:0.7},colors:['#ffd700','#ff9800','#e91e63']});
            } else {
                btn.classList.remove('claimed');c.textContent='⬜';sc-=p;nc--;
            }
            s.textContent=sc;n.textContent=nc+' / 24 badges';
        }finally{btn.disabled=false;}
    }
    </script></body></html>
    """);

    return Results.Content(html.ToString(), "text/html");
});

// ─── MY TEAM ───

app.MapGet("/my", (HttpContext ctx) =>
{
    var teamId = GetTeamId(ctx);
    return teamId is not null ? Results.Redirect($"/team/{teamId}") : Results.Redirect("/login");
});

// ─── LOGIN ───

app.MapGet("/login", (HttpContext ctx) =>
{
    var name = ctx.Request.Query["team"].FirstOrDefault() ?? "";
    var pin = ctx.Request.Query["pin"].FirstOrDefault() ?? "";
    var html = new StringBuilder();
    html.Append(PageHead("Logg inn", ctx));
    html.Append($$"""
    <div class="container">
        <h1>🔐 Logg inn</h1>
        <div class="login-box">
            <div class="login-section">
                <h2>Lag-innlogging</h2>
                <p class="login-hint">Skriv inn lagnavnet og PIN-koden dere fikk av admin</p>
                <input type="text" id="tn" placeholder="Lagnavn..." maxlength="50" value="{{Esc(name)}}" />
                <input type="text" id="tp" placeholder="PIN-kode (6 siffer)..." maxlength="6" value="{{Esc(pin)}}" />
                <button onclick="teamLogin()">Logg inn</button>
                <div id="tmsg"></div>
            </div>
            <div class="login-divider"></div>
            <div class="login-section">
                <h2>Admin-innlogging</h2>
                <p class="login-hint">For arrangører som skal opprette lag</p>
                <input type="text" id="ap" placeholder="Admin-PIN..." maxlength="20" />
                <button onclick="adminLogin()">Logg inn som admin</button>
                <div id="amsg"></div>
            </div>
        </div>
    </div>
    <script>
    async function teamLogin(){
        const n=document.getElementById('tn').value.trim(),p=document.getElementById('tp').value.trim();
        if(!n||!p)return;
        const r=await fetch('/api/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:n,pin:p})});
        if(r.ok){location.href='/my';}
        else{const e=await r.json();document.getElementById('tmsg').innerHTML=`<span class="err">${e.error}</span>`;}
    }
    async function adminLogin(){
        const p=document.getElementById('ap').value.trim();
        if(!p)return;
        const r=await fetch('/api/admin-login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({pin:p})});
        if(r.ok){location.href='/admin';}
        else{const e=await r.json();document.getElementById('amsg').innerHTML=`<span class="err">${e.error}</span>`;}
    }
    document.getElementById('tp')?.addEventListener('keydown',e=>{if(e.key==='Enter')teamLogin();});
    document.getElementById('ap')?.addEventListener('keydown',e=>{if(e.key==='Enter')adminLogin();});
    </script></body></html>
    """);
    return Results.Content(html.ToString(), "text/html");
});

// ─── LOGOUT ───

app.MapGet("/logout", (HttpContext ctx) =>
{
    ctx.Response.Cookies.Delete(CookieName);
    ctx.Response.Cookies.Delete("hackathon_admin");
    return Results.Redirect("/");
});

// ─── HISTORY PAGE ───

app.MapGet("/history", async (HackathonDb db, HttpContext ctx) =>
{
    var html = new StringBuilder();
    html.Append(PageHead("Poengutvikling", ctx));
    html.Append("""
    <div class="container">
        <h1>📈 Poengutvikling</h1>
        <canvas id="chart" height="400"></canvas>
    </div>
    <script src="https://cdn.jsdelivr.net/npm/chart.js@4/dist/chart.umd.min.js"></script>
    <script>
    (async()=>{
        const r=await fetch('/api/history');const data=await r.json();
        const colors=['#e91e63','#ffc107','#9c27b0','#2196f3','#ff9800','#8bc34a','#ffd700','#00bcd4','#f44336','#4caf50'];
        const datasets=data.map((team,i)=>({
            label:team.teamName,
            data:team.data.map(d=>({x:new Date(d.t),y:d.score})),
            borderColor:colors[i%colors.length],
            backgroundColor:'transparent',
            tension:0.3,pointRadius:2
        }));
        new Chart(document.getElementById('chart'),{
            type:'line',
            data:{datasets},
            options:{
                responsive:true,
                scales:{x:{type:'time',time:{unit:'minute',displayFormats:{minute:'HH:mm'}},title:{display:true,text:'Tid',color:'#8b949e'},ticks:{color:'#8b949e'},grid:{color:'#21262d'}},y:{title:{display:true,text:'Poeng',color:'#8b949e'},ticks:{color:'#8b949e'},grid:{color:'#21262d'},min:0}},
                plugins:{legend:{labels:{color:'#e6edf3'}}}
            }
        });
    })();
    </script></body></html>
    """);
    return Results.Content(html.ToString(), "text/html");
});

// ─── ADMIN ───

app.MapGet("/admin", async (HackathonDb db, HttpContext ctx) =>
{
    if (!IsAdmin(ctx))
        return Results.Redirect("/login");

    var teams = await db.Teams.Include(t => t.TeamBadges).ThenInclude(tb => tb.Badge).OrderBy(t => t.Name).ToListAsync();
    var html = new StringBuilder();
    html.Append(PageHead("Admin", ctx));
    html.Append("""
    <div class="container">
        <h1>⚙️ Admin — Lag</h1>
        <div class="admin-form">
            <input type="text" id="tn" placeholder="Lagnavn..." maxlength="50" />
            <div class="emoji-picker-wrap">
                <button class="emoji-pick-btn" id="emoji-btn" onclick="document.getElementById('emoji-grid').classList.toggle('hidden')">🚀</button>
                <div id="emoji-grid" class="emoji-grid hidden"></div>
            </div>
            <button onclick="go()">Opprett lag</button>
        </div>
        <div id="msg"></div>
        <div class="team-list">
    """);

    foreach (var t in teams)
    {
        html.Append($"""
            <div class="team-card" id="t-{t.Id}">
                <span class="team-emoji-display">{t.Emoji}</span>
                <a href="/team/{t.Id}" class="team-link">{Esc(t.Name)}</a>
                <span class="team-pin">PIN: <code>{t.Pin}</code></span>
                <span class="team-meta">{t.TeamBadges.Sum(tb => tb.Badge.Points)}p</span>
                <button class="qr-btn" onclick="showQr('{Esc(t.Name)}','{t.Pin}')">📱</button>
                <button class="delete-btn" onclick="del({t.Id})">🗑️</button>
            </div>
        """);
    }

    if (teams.Count == 0)
        html.Append("""<p class="empty">Ingen lag ennå.</p>""");

    html.Append("""
        </div>
        <div id="qr-modal" class="modal hidden" onclick="this.classList.add('hidden')">
            <div class="modal-content" onclick="event.stopPropagation()">
                <h3 id="qr-title"></h3>
                <div id="qr-canvas"></div>
                <p id="qr-url" class="qr-url"></p>
            </div>
        </div>
    </div>
    <script src="https://cdn.jsdelivr.net/npm/qrcode/build/qrcode.min.js"></script>
    <script>
    const emojis=['🚀','⚡','🔥','💎','🎯','🦊','🐉','🌊','⭐','🎪','🏴‍☠️','🦅','🐺','🌋','🎸','🏔️','🦁','🐙','🌟','🎲','🧊','🦈','🔮','🛸','🎭','🌈','🍀','🎪','🏹','⚔️'];
    const grid=document.getElementById('emoji-grid');
    emojis.forEach(e=>{const b=document.createElement('button');b.textContent=e;b.className='emoji-opt';b.onclick=()=>{document.getElementById('emoji-btn').textContent=e;grid.classList.add('hidden');};grid.appendChild(b);});
    let selectedEmoji='🚀';
    async function go(){
        const i=document.getElementById('tn'),n=i.value.trim();
        selectedEmoji=document.getElementById('emoji-btn').textContent;
        if(!n)return;
        const r=await fetch('/api/teams',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:n,emoji:selectedEmoji})});
        const m=document.getElementById('msg');
        if(r.ok){const d=await r.json();m.innerHTML=`<span style="color:#4caf50">✅ ${d.emoji} ${d.name} — PIN: <code>${d.pin}</code></span>`;i.value='';setTimeout(()=>location.reload(),1500);}
        else{const e=await r.json();m.innerHTML=`<span class="err">❌ ${e.error}</span>`;}
    }
    document.getElementById('tn').addEventListener('keydown',e=>{if(e.key==='Enter')go();});
    async function del(id){if(!confirm('Slett laget?'))return;await fetch(`/api/teams/${id}`,{method:'DELETE'});document.getElementById(`t-${id}`)?.remove();}
    function showQr(name,pin){
        const url=`${location.origin}/login?team=${encodeURIComponent(name)}&pin=${pin}`;
        document.getElementById('qr-title').textContent=name;
        document.getElementById('qr-url').textContent=url;
        const canvas=document.getElementById('qr-canvas');
        canvas.innerHTML='';
        QRCode.toCanvas(document.createElement('canvas'),url,{width:200,margin:2,color:{dark:'#e6edf3',light:'#0d1117'}},function(err,c){canvas.appendChild(c);});
        document.getElementById('qr-modal').classList.remove('hidden');
    }
    </script></body></html>
    """);

    return Results.Content(html.ToString(), "text/html");
});

// ─── ADMIN QR PRINT PAGE ───

app.MapGet("/admin/qr", async (HackathonDb db, HttpContext ctx) =>
{
    if (!IsAdmin(ctx))
        return Results.Redirect("/login");

    var teams = await db.Teams.OrderBy(t => t.Name).ToListAsync();
    var html = new StringBuilder();
    html.Append(PageHead("QR-koder", ctx));
    html.Append("""
    <div class="container">
        <h1>📱 QR-koder for alle lag</h1>
        <p class="subtitle">Skriv ut denne siden (Ctrl+P) og klipp ut QR-kodene</p>
        <div class="qr-print-grid" id="qr-grid"></div>
    </div>
    <script src="https://cdn.jsdelivr.net/npm/qrcode/build/qrcode.min.js"></script>
    <script>
    const teams=TEAMS_JSON;
    const grid=document.getElementById('qr-grid');
    teams.forEach(t=>{
        const url=`${location.origin}/login?team=${encodeURIComponent(t.name)}&pin=${t.pin}`;
        const card=document.createElement('div');card.className='qr-print-card';
        card.innerHTML=`<h3>${t.emoji} ${t.name}</h3><div class="qr-img"></div><p class="qr-pin">PIN: ${t.pin}</p>`;
        grid.appendChild(card);
        QRCode.toCanvas(document.createElement('canvas'),url,{width:150,margin:1,color:{dark:'#000',light:'#fff'}},function(err,c){card.querySelector('.qr-img').appendChild(c);});
    });
    </script></body></html>
    """.Replace("TEAMS_JSON", JsonSerializer.Serialize(teams.Select(t => new { t.Name, t.Pin, t.Emoji }))));

    return Results.Content(html.ToString(), "text/html");
});

app.Run();

// ─── HELPERS ───

static string Esc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

static string PageHead(string title, HttpContext ctx, bool autoRefresh = false)
{
    int? teamId = null;
    bool isAdmin = false;
    try
    {
        var dp = ctx.RequestServices.GetRequiredService<IDataProtectionProvider>();
        var c1 = ctx.Request.Cookies["hackathon_team"];
        if (c1 is not null) teamId = int.Parse(dp.CreateProtector("hackathon").Unprotect(c1));
        var c2 = ctx.Request.Cookies["hackathon_admin"];
        if (c2 is not null) isAdmin = dp.CreateProtector("hackathon-admin").Unprotect(c2) == "admin";
    }
    catch { }

    var myTeamLink = teamId is not null ? $"""<a href="/team/{teamId}">👥 Mitt lag</a>""" : "";
    var adminLink = isAdmin ? """<a href="/admin">⚙️ Admin</a>""" : "";
    var authLink = (teamId is not null || isAdmin)
        ? """<a href="/logout" class="logout">Logg ut</a>"""
        : """<a href="/login">🔐 Logg inn</a>""";

    return $$$"""
    <!DOCTYPE html><html lang="no"><head>
    <meta charset="utf-8"/><meta name="viewport" content="width=device-width,initial-scale=1"/>
    <title>{{{Esc(title)}}}</title>
    <style>
    :root{--bg:#0d1117;--card:#161b22;--border:#30363d;--text:#e6edf3;--muted:#8b949e;--hover:#21262d}
    .light-mode{--bg:#f6f8fa;--card:#ffffff;--border:#d0d7de;--text:#1f2328;--muted:#656d76;--hover:#eaeef2}
    *{margin:0;padding:0;box-sizing:border-box}
    body{background:var(--bg);color:var(--text);font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif;min-height:100vh;transition:background .3s,color .3s}
    .navbar{display:flex;align-items:center;gap:12px;padding:12px 24px;background:var(--card);border-bottom:1px solid var(--border);font-size:.95rem;flex-wrap:wrap}
    .navbar a{color:var(--muted);text-decoration:none;padding:6px 12px;border-radius:6px;transition:all .15s}
    .navbar a:hover{color:var(--text);background:var(--hover)}
    .navbar .spacer{flex:1}
    .navbar .logout{color:#f85149;font-size:.85rem}
    .navbar button{background:none;border:none;cursor:pointer;font-size:1.2rem;padding:4px 8px;border-radius:4px}
    .navbar button:hover{background:var(--hover)}
    #countdown{font-family:'Courier New',monospace;font-size:.9rem;color:#ffd700;background:rgba(255,215,0,.1);padding:4px 12px;border-radius:6px;font-weight:700}
    .container{max-width:1100px;margin:0 auto;padding:24px 16px}
    h1{text-align:center;font-size:2.5rem;margin-bottom:32px;background:linear-gradient(135deg,#ffd700,#ff9800);-webkit-background-clip:text;-webkit-text-fill-color:transparent;background-clip:text}
    .subtitle{text-align:center;color:var(--muted);margin-top:-20px;margin-bottom:32px}
    .back-link{color:#58a6ff;text-decoration:none;display:inline-block;margin-bottom:16px;font-size:.9rem}
    .back-link:hover{text-decoration:underline}
    .podium-wrapper{display:grid;grid-template-columns:1fr 1fr 1fr;gap:16px;align-items:end;margin-bottom:32px;min-height:320px}
    .podium-card{background:var(--card);border-radius:16px;padding:28px 20px;text-align:center;border:2px solid var(--border);transition:transform .2s;position:relative}
    .podium-card:hover{transform:translateY(-4px)}
    .podium-card.gold{border-color:#ffd700;box-shadow:0 0 40px rgba(255,215,0,.2);min-height:280px}
    .podium-card.silver{border-color:#c0c0c0;box-shadow:0 0 20px rgba(192,192,192,.1);min-height:240px;margin-top:40px}
    .podium-card.bronze{border-color:#cd7f32;box-shadow:0 0 20px rgba(205,127,50,.1);min-height:200px;margin-top:80px}
    .podium-rank{position:absolute;top:-16px;left:50%;transform:translateX(-50%);width:32px;height:32px;border-radius:50%;display:flex;align-items:center;justify-content:center;font-weight:800;font-size:.9rem}
    .gold .podium-rank{background:#ffd700;color:#000}
    .silver .podium-rank{background:#c0c0c0;color:#000}
    .bronze .podium-rank{background:#cd7f32;color:#fff}
    .podium-emoji{font-size:2.5rem;margin-bottom:8px;margin-top:8px}
    .podium-name{font-size:1.3rem;font-weight:700;margin-bottom:4px}
    .podium-score{font-size:2.2rem;font-weight:800;color:#ffd700}
    .podium-badges{color:var(--muted);margin-top:4px;margin-bottom:12px;font-size:.9rem}
    .progress-ring{width:56px;height:56px;margin:8px auto 0;transform:rotate(-90deg)}
    .ring-bg{fill:none;stroke:var(--border);stroke-width:4}
    .ring-fill{fill:none;stroke:#ffd700;stroke-width:4;stroke-linecap:round;stroke-dasharray:176;transition:stroke-dashoffset .5s}
    .expand-section{text-align:center;margin:24px 0}
    .expand-btn{background:var(--card);border:1px solid var(--border);color:var(--text);padding:12px 24px;border-radius:8px;cursor:pointer;font-size:1rem;transition:all .15s}
    .expand-btn:hover{background:var(--hover);border-color:#58a6ff}
    .hidden{display:none!important}
    .progress-bar{background:var(--hover);border-radius:8px;height:10px;overflow:hidden}
    .progress-fill{height:100%;background:linear-gradient(90deg,#ffd700,#ff9800);border-radius:8px;transition:width .5s}
    .table-bar{height:8px;min-width:120px}
    .ranking{width:100%;border-collapse:collapse;margin-top:16px}
    .ranking th{padding:12px 16px;text-align:left;border-bottom:2px solid var(--border);color:var(--muted);font-size:.85rem;text-transform:uppercase}
    .ranking td{padding:14px 16px;border-bottom:1px solid var(--hover)}
    .ranking tr:hover{background:var(--card)}
    .ranking .score{font-weight:700;color:#ffd700;font-size:1.2rem}
    .activity-feed{background:var(--card);border-radius:12px;padding:20px;margin-top:32px;border:1px solid var(--border)}
    .activity-feed h3{margin-bottom:12px;color:var(--muted);font-size:.95rem}
    .feed-item{padding:8px 0;border-bottom:1px solid var(--hover);font-size:.9rem;color:var(--muted);animation:none}
    .feed-item.feed-new{animation:flash .5s ease-out}
    .feed-item.claimed strong{color:#4caf50}
    .feed-item.unclaimed strong{color:#f44336}
    .feed-time{color:var(--muted);font-family:monospace;margin-right:8px}
    .feed-empty{color:var(--muted);font-style:italic;padding:12px 0}
    @keyframes flash{from{background:rgba(255,215,0,.15)}to{background:transparent}}
    .team-score{text-align:center;margin-bottom:32px;background:var(--card);border-radius:12px;padding:24px}
    .score-number{font-size:3rem;font-weight:800;color:#ffd700}
    .score-label{font-size:1.2rem;color:var(--muted)}
    .badge-count{display:block;color:var(--muted);margin-top:4px}
    .category-group{margin-bottom:24px}
    .category-title{font-size:1.1rem;margin-bottom:12px;color:var(--text)}
    .badges{display:grid;grid-template-columns:repeat(auto-fill,minmax(200px,1fr));gap:8px}
    .badge-btn{display:flex;align-items:center;gap:8px;padding:12px 16px;background:var(--card);border:2px solid var(--border);border-radius:10px;color:var(--text);cursor:pointer;font-size:.95rem;transition:all .15s;text-align:left;width:100%}
    .badge-btn:hover{border-color:var(--cat-color,#666);background:var(--hover)}
    .badge-btn.claimed{border-color:var(--cat-color,#666);background:color-mix(in srgb,var(--cat-color,#666) 12%,var(--card))}
    .badge-check{font-size:1.1rem;flex-shrink:0}
    .badge-name{flex:1;font-weight:500}
    .badge-points{font-weight:700;color:var(--cat-color,#666);font-size:.85rem}
    .gallery-category{margin-bottom:32px}
    .gallery-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:20px}
    .gallery-card{display:flex;flex-direction:column;align-items:center;text-decoration:none;color:var(--text);padding:24px 16px;background:var(--card);border:2px solid var(--border);border-radius:16px;transition:all .2s}
    .gallery-card:hover{border-color:var(--cat-color);transform:translateY(-4px);box-shadow:0 8px 24px rgba(0,0,0,.3)}
    .badge-circle{width:120px;height:120px;border-radius:50%;border:4px solid;display:flex;align-items:center;justify-content:center;background:radial-gradient(circle,rgba(255,255,255,.05) 0%,transparent 70%);margin-bottom:16px}
    .badge-icon{font-size:3rem}
    .gallery-name{font-weight:700;font-size:1.1rem;margin-bottom:4px;text-align:center}
    .gallery-points{font-weight:800;font-size:.85rem;letter-spacing:1px}
    .detail-layout{display:grid;grid-template-columns:320px 1fr;gap:48px;align-items:start}
    .detail-left{display:flex;flex-direction:column;align-items:center;text-align:center}
    .badge-circle-lg{width:200px;height:200px;border-radius:50%;border:6px solid;display:flex;align-items:center;justify-content:center;background:radial-gradient(circle,rgba(255,255,255,.08) 0%,transparent 70%);margin-bottom:24px}
    .badge-icon-lg{font-size:5rem}
    .detail-name{font-size:2rem;font-weight:800;-webkit-text-fill-color:unset;background:none}
    .detail-points-box{display:inline-block;padding:8px 24px;border:2px solid;border-radius:8px;font-weight:800;font-size:1rem;letter-spacing:1px}
    .detail-right{display:flex;flex-direction:column;gap:20px}
    .detail-subtitle{color:var(--muted);font-size:1.1rem}
    .detail-section{background:var(--card);border-radius:12px;padding:24px;border-left:4px solid var(--border)}
    .section-header{font-size:1rem;margin-bottom:12px;text-transform:uppercase;letter-spacing:.5px}
    .detail-section p{color:#c9d1d9;line-height:1.6}
    .criteria-list{list-style:none;display:flex;flex-direction:column;gap:12px}
    .criteria-list li{color:#c9d1d9;font-size:1rem}
    .bonus-box{background:rgba(255,215,0,.08);border:1px solid rgba(255,215,0,.3);border-radius:12px;padding:20px 24px;color:#ffd700;font-weight:600}
    .placeholder-section{text-align:center;color:var(--muted);padding:48px 24px}
    .placeholder-hint{font-size:.9rem;margin-top:8px;color:#6e7681}
    .login-box{display:grid;grid-template-columns:1fr auto 1fr;gap:32px;max-width:800px;margin:0 auto}
    .login-section{display:flex;flex-direction:column;gap:12px}
    .login-section h2{color:var(--text);font-size:1.2rem}
    .login-hint{color:var(--muted);font-size:.9rem}
    .login-section input{padding:12px 16px;background:var(--card);border:2px solid var(--border);border-radius:8px;color:var(--text);font-size:1rem}
    .login-section input:focus{outline:none;border-color:#58a6ff}
    .login-section button{padding:12px 24px;background:#238636;border:none;border-radius:8px;color:#fff;font-size:1rem;font-weight:600;cursor:pointer}
    .login-section button:hover{background:#2ea043}
    .login-divider{width:1px;background:var(--border)}
    .err{color:#f85149}
    .admin-form{display:flex;gap:12px;margin-bottom:20px;align-items:center;flex-wrap:wrap}
    .admin-form input{flex:1;padding:12px 16px;background:var(--card);border:2px solid var(--border);border-radius:8px;color:var(--text);font-size:1rem;min-width:200px}
    .admin-form input:focus{outline:none;border-color:#58a6ff}
    .admin-form>button{padding:12px 24px;background:#238636;border:none;border-radius:8px;color:#fff;font-size:1rem;font-weight:600;cursor:pointer}
    .admin-form>button:hover{background:#2ea043}
    .emoji-picker-wrap{position:relative}
    .emoji-pick-btn{font-size:1.5rem;padding:8px 12px;background:var(--card);border:2px solid var(--border);border-radius:8px;cursor:pointer}
    .emoji-grid{position:absolute;top:100%;left:0;background:var(--card);border:1px solid var(--border);border-radius:8px;padding:8px;display:grid;grid-template-columns:repeat(6,1fr);gap:4px;z-index:100;box-shadow:0 8px 24px rgba(0,0,0,.4)}
    .emoji-opt{font-size:1.3rem;padding:6px;background:none;border:none;cursor:pointer;border-radius:4px}
    .emoji-opt:hover{background:var(--hover)}
    #msg{margin-bottom:16px;font-size:.95rem}
    .team-list{display:flex;flex-direction:column;gap:8px}
    .team-card{display:flex;align-items:center;gap:12px;padding:14px 18px;background:var(--card);border-radius:10px;border:1px solid var(--border)}
    .team-emoji-display{font-size:1.5rem}
    .team-link{flex:1;color:#58a6ff;text-decoration:none;font-weight:600;font-size:1.1rem}
    .team-link:hover{text-decoration:underline}
    .team-pin{color:var(--muted);font-size:.85rem}
    .team-pin code{background:var(--hover);padding:2px 8px;border-radius:4px;color:#ffd700;font-weight:600;letter-spacing:2px}
    .team-meta{color:var(--muted)}
    .qr-btn,.delete-btn{background:none;border:none;cursor:pointer;font-size:1.2rem;opacity:.6}
    .qr-btn:hover,.delete-btn:hover{opacity:1}
    .modal{position:fixed;inset:0;background:rgba(0,0,0,.7);display:flex;align-items:center;justify-content:center;z-index:1000}
    .modal-content{background:var(--card);border:1px solid var(--border);border-radius:16px;padding:32px;text-align:center;min-width:280px}
    .modal-content h3{margin-bottom:16px}
    .qr-url{font-size:.75rem;color:var(--muted);margin-top:12px;word-break:break-all}
    .qr-print-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(200px,1fr));gap:24px}
    .qr-print-card{background:var(--card);border:1px solid var(--border);border-radius:12px;padding:20px;text-align:center}
    .qr-print-card h3{margin-bottom:12px}
    .qr-pin{margin-top:8px;color:var(--muted)}
    .empty{text-align:center;color:var(--muted);padding:40px}
    .empty code{background:var(--card);padding:2px 8px;border-radius:4px}
    .footer{display:flex;justify-content:space-between;align-items:center;margin-top:40px;padding-top:20px;border-top:1px solid var(--hover);color:var(--muted);font-size:.85rem}
    .footer a{color:#58a6ff;text-decoration:none}
    .footer a:hover{text-decoration:underline}
    @media print{.navbar,.expand-btn{display:none}.qr-print-card{break-inside:avoid;border:1px solid #000}}
    @media(max-width:700px){h1{font-size:1.6rem}.podium-wrapper{grid-template-columns:1fr;gap:12px}.podium-card.silver,.podium-card.bronze{margin-top:0}.badges{grid-template-columns:1fr}.admin-form{flex-direction:column}.detail-layout{grid-template-columns:1fr}.login-box{grid-template-columns:1fr;gap:16px}.login-divider{width:100%;height:1px}.gallery-grid{grid-template-columns:repeat(auto-fill,minmax(140px,1fr))}}
    </style></head><body>
    <nav class="navbar">
        <a href="/">🏆 Scoreboard</a>
        <a href="/badges">🏅 Badges</a>
        <a href="/history">📈 Historikk</a>
        {{{myTeamLink}}}
        <span id="countdown"></span>
        <span class="spacer"></span>
        <button id="sound-toggle" title="Lyd av/på">🔇</button>
        <button onclick="document.body.classList.toggle('light-mode');localStorage.setItem('theme',document.body.classList.contains('light-mode')?'light':'dark')" title="Bytt tema">🌓</button>
        {{{adminLink}}}
        {{{authLink}}}
    </nav>
    <script>
    if(localStorage.getItem('theme')==='light')document.body.classList.add('light-mode');
    (function countdown(){
        const end=new Date('2026-05-15T16:00:00').getTime();
        const el=document.getElementById('countdown');
        if(!el)return;
        function tick(){
            const now=Date.now(),diff=end-now;
            if(diff<=0){el.textContent='⏰ TIDEN ER UTE!';el.style.color='#f85149';return;}
            const h=Math.floor(diff/3600000),m=Math.floor((diff%3600000)/60000),s=Math.floor((diff%60000)/1000);
            el.textContent=`⏱ ${String(h).padStart(2,'0')}:${String(m).padStart(2,'0')}:${String(s).padStart(2,'0')}`;
            requestAnimationFrame(tick);
        }
        tick();
    })();
    </script>
    """;
}

// ─── BACKGROUND SERVICE: Score Snapshots ───

public class SnapshotService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    public SnapshotService(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<HackathonDb>();
                var teams = await db.Teams.Include(t => t.TeamBadges).ThenInclude(tb => tb.Badge).ToListAsync(ct);
                var now = DateTime.UtcNow;
                foreach (var t in teams)
                {
                    db.ScoreSnapshots.Add(new ScoreSnapshot
                    {
                        TeamId = t.Id,
                        TeamName = t.Name,
                        Score = t.TeamBadges.Sum(tb => tb.Badge.Points),
                        Timestamp = now
                    });
                }
                await db.SaveChangesAsync(ct);
            }
            catch { }
        }
    }
}
