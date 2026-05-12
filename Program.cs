using Hackathon.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<HackathonDb>(options =>
    options.UseSqlite("Data Source=hackathon.db"));
builder.Services.AddDataProtection();

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

    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { error = "Team name is required" });

    if (await db.Teams.AnyAsync(t => t.Name == name))
        return Results.Conflict(new { error = "Team already exists" });

    var team = new Team { Name = name, Pin = GeneratePin() };
    db.Teams.Add(team);
    await db.SaveChangesAsync();
    return Results.Created($"/api/teams/{team.Id}", new { team.Id, team.Name, team.Pin });
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

    const int maxScore = 6700;
    var html = new StringBuilder();
    html.Append(PageHead("Hackathon Scoreboard", ctx, autoRefresh: 5));
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
        html.Append("""<p class="empty">Ingen lag registrert ennå.</p>""");

    html.Append("""
        <div class="footer">
            <span class="max-score">Maks: 6 700 poeng · 24 badges</span>
        </div>
    </div></body></html>
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
    {
        html.Append($"""<div class="bonus-box">{Esc(badge.BonusText)}</div>""");
    }

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
            if(r.status===403){alert('Du har ikke tilgang til å endre badges for dette laget');return;}
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

// ─── MY TEAM (redirect) ───

app.MapGet("/my", (HttpContext ctx) =>
{
    var teamId = GetTeamId(ctx);
    return teamId is not null ? Results.Redirect($"/team/{teamId}") : Results.Redirect("/login");
});

// ─── LOGIN PAGE ───

app.MapGet("/login", (HttpContext ctx) =>
{
    var html = new StringBuilder();
    html.Append(PageHead("Logg inn", ctx));
    html.Append("""
    <div class="container">
        <h1>🔐 Logg inn</h1>
        <div class="login-box">
            <div class="login-section">
                <h2>Lag-innlogging</h2>
                <p class="login-hint">Skriv inn lagnavnet og PIN-koden dere fikk av admin</p>
                <input type="text" id="tn" placeholder="Lagnavn..." maxlength="50" />
                <input type="text" id="tp" placeholder="PIN-kode (6 siffer)..." maxlength="6" />
                <button onclick="teamLogin()">Logg inn</button>
                <div id="tmsg"></div>
            </div>
            <div class="login-divider"></div>
            <div class="login-section">
                <h2>Admin-innlogging</h2>
                <p class="login-hint">For arrangører som skal opprette lag og administrere</p>
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

// ─── ADMIN (auth required) ───

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
                <span class="team-pin">PIN: <code>{t.Pin}</code></span>
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
        if(r.ok){const d=await r.json();m.innerHTML=`<span style="color:#4caf50">✅ ${d.name} opprettet — PIN: <code>${d.pin}</code></span>`;i.value='';setTimeout(()=>location.reload(),1500);}
        else{const e=await r.json();m.innerHTML=`<span class="err">❌ ${e.error}</span>`;}
    }
    document.getElementById('tn').addEventListener('keydown',e=>{if(e.key==='Enter')go();});
    async function del(id){if(!confirm('Slett laget?'))return;await fetch(`/api/teams/${id}`,{method:'DELETE'});document.getElementById(`t-${id}`)?.remove();}
    </script></body></html>
    """);

    return Results.Content(html.ToString(), "text/html");
});

app.Run();

static string Esc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

static string PageHead(string title, HttpContext ctx, int autoRefresh = 0)
{
    var refresh = autoRefresh > 0 ? $"""<meta http-equiv="refresh" content="{autoRefresh}">""" : "";

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
        ? """<a href="/logout">Logg ut</a>"""
        : """<a href="/login">🔐 Logg inn</a>""";

    return $$$"""
    <!DOCTYPE html><html lang="no"><head>
    <meta charset="utf-8"/><meta name="viewport" content="width=device-width,initial-scale=1"/>
    {{{refresh}}}
    <title>{{{title}}}</title>
    <style>
    *{margin:0;padding:0;box-sizing:border-box}
    body{background:#0d1117;color:#e6edf3;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif;min-height:100vh}
    .navbar{display:flex;align-items:center;gap:16px;padding:12px 24px;background:#161b22;border-bottom:1px solid #30363d;font-size:.95rem;flex-wrap:wrap}
    .navbar a{color:#8b949e;text-decoration:none;padding:6px 12px;border-radius:6px;transition:all .15s}
    .navbar a:hover,.navbar a.active{color:#e6edf3;background:#21262d}
    .navbar .spacer{flex:1}
    .navbar .logout{color:#f85149;font-size:.85rem}
    .container{max-width:1100px;margin:0 auto;padding:24px 16px}
    h1{text-align:center;font-size:2.5rem;margin-bottom:32px;background:linear-gradient(135deg,#ffd700,#ff9800);-webkit-background-clip:text;-webkit-text-fill-color:transparent;background-clip:text}
    .subtitle{text-align:center;color:#8b949e;margin-top:-20px;margin-bottom:32px}
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
    .gallery-category{margin-bottom:32px}
    .gallery-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:20px}
    .gallery-card{display:flex;flex-direction:column;align-items:center;text-decoration:none;color:#e6edf3;padding:24px 16px;background:#161b22;border:2px solid #30363d;border-radius:16px;transition:all .2s}
    .gallery-card:hover{border-color:var(--cat-color);transform:translateY(-4px);box-shadow:0 8px 24px rgba(0,0,0,.3)}
    .badge-circle{width:120px;height:120px;border-radius:50%;border:4px solid;display:flex;align-items:center;justify-content:center;background:radial-gradient(circle,rgba(255,255,255,.05) 0%,transparent 70%);margin-bottom:16px}
    .badge-icon{font-size:3rem}
    .gallery-name{font-weight:700;font-size:1.1rem;margin-bottom:4px;text-align:center}
    .gallery-points{font-weight:800;font-size:.85rem;letter-spacing:1px}
    .detail-layout{display:grid;grid-template-columns:320px 1fr;gap:48px;align-items:start}
    .detail-left{display:flex;flex-direction:column;align-items:center;text-align:center}
    .badge-circle-lg{width:200px;height:200px;border-radius:50%;border:6px solid;display:flex;align-items:center;justify-content:center;background:radial-gradient(circle,rgba(255,255,255,.08) 0%,transparent 70%);margin-bottom:24px}
    .badge-icon-lg{font-size:5rem}
    .detail-name{font-size:2rem;font-weight:800;margin-bottom:12px;-webkit-text-fill-color:unset;background:none}
    .detail-points-box{display:inline-block;padding:8px 24px;border:2px solid;border-radius:8px;font-weight:800;font-size:1rem;letter-spacing:1px}
    .detail-right{display:flex;flex-direction:column;gap:20px}
    .detail-subtitle{color:#8b949e;font-size:1.1rem;margin-bottom:8px}
    .detail-section{background:#161b22;border-radius:12px;padding:24px;border-left:4px solid #30363d}
    .section-header{font-size:1rem;margin-bottom:12px;text-transform:uppercase;letter-spacing:.5px}
    .detail-section p{color:#c9d1d9;line-height:1.6}
    .criteria-list{list-style:none;display:flex;flex-direction:column;gap:12px}
    .criteria-list li{color:#c9d1d9;font-size:1rem;padding-left:4px}
    .bonus-box{background:rgba(255,215,0,.08);border:1px solid rgba(255,215,0,.3);border-radius:12px;padding:20px 24px;color:#ffd700;font-weight:600;font-size:1rem}
    .placeholder-section{text-align:center;color:#8b949e;padding:48px 24px}
    .placeholder-hint{font-size:.9rem;margin-top:8px;color:#6e7681}
    .login-box{display:grid;grid-template-columns:1fr auto 1fr;gap:32px;max-width:800px;margin:0 auto}
    .login-section{display:flex;flex-direction:column;gap:12px}
    .login-section h2{color:#e6edf3;font-size:1.2rem}
    .login-hint{color:#8b949e;font-size:.9rem}
    .login-section input{padding:12px 16px;background:#161b22;border:2px solid #30363d;border-radius:8px;color:#e6edf3;font-size:1rem}
    .login-section input:focus{outline:none;border-color:#58a6ff}
    .login-section button{padding:12px 24px;background:#238636;border:none;border-radius:8px;color:#fff;font-size:1rem;font-weight:600;cursor:pointer}
    .login-section button:hover{background:#2ea043}
    .login-divider{width:1px;background:#30363d}
    .err{color:#f85149}
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
    .team-pin{color:#8b949e;font-size:.85rem}
    .team-pin code{background:#21262d;padding:2px 8px;border-radius:4px;color:#ffd700;font-weight:600;letter-spacing:2px}
    .team-meta{color:#8b949e}
    .delete-btn{background:none;border:none;cursor:pointer;font-size:1.2rem;opacity:.5}
    .delete-btn:hover{opacity:1}
    .empty{text-align:center;color:#8b949e;padding:40px}
    .empty code{background:#161b22;padding:2px 8px;border-radius:4px}
    .footer{display:flex;justify-content:space-between;align-items:center;margin-top:40px;padding-top:20px;border-top:1px solid #21262d;color:#8b949e;font-size:.85rem}
    .footer a{color:#58a6ff;text-decoration:none}
    .footer a:hover{text-decoration:underline}
    @media(max-width:700px){h1{font-size:1.6rem}.podium{flex-direction:column;align-items:center}.podium-card{min-width:unset;width:100%}.badges{grid-template-columns:1fr}.admin-form{flex-direction:column}.detail-layout{grid-template-columns:1fr}.login-box{grid-template-columns:1fr;gap:16px}.login-divider{width:100%;height:1px}.gallery-grid{grid-template-columns:repeat(auto-fill,minmax(140px,1fr))}}
    </style></head><body>
    <nav class="navbar">
        <a href="/">🏆 Scoreboard</a>
        <a href="/badges">🏅 Badges</a>
        {{{myTeamLink}}}
        <span class="spacer"></span>
        {{{adminLink}}}
        {{{authLink}}}
    </nav>
    """;
}
