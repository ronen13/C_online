using Microsoft.EntityFrameworkCore;
using QuizSystem.Data;
using QuizSystem.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddSession(o =>
{
    o.IdleTimeout = TimeSpan.FromHours(8);
    o.Cookie.HttpOnly = true;
    o.Cookie.IsEssential = true;
});

// ── Database ──────────────────────────────────────────────────────────────────
// On Render: DATABASE_PATH=/data/quiz.db  (persistent disk mounted at /data)
// Local dev: quiz.db in project root
var dbPath = Environment.GetEnvironmentVariable("DATABASE_PATH")
             ?? Path.Combine(Directory.GetCurrentDirectory(), "quiz.db");

// Ensure the directory exists (important for /data on Render first boot)
var dbDir = Path.GetDirectoryName(dbPath)!;
if (!Directory.Exists(dbDir))
    Directory.CreateDirectory(dbDir);

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddHttpClient<QuestionGeneratorService>();
builder.Services.AddScoped<QuestionGeneratorService>();

var app = builder.Build();

// ── Auto-migrate DB on startup ────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();   // creates tables if DB file is new
    Console.WriteLine($"[DB] SQLite path: {dbPath}");
    Console.WriteLine($"[DB] File exists: {File.Exists(dbPath)}");
}

app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

app.Run();
