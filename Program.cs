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

// SQLite path: use /data/quiz.db on Render, local otherwise
var dbPath = Environment.GetEnvironmentVariable("DATABASE_PATH") ?? "quiz.db";
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddHttpClient<QuestionGeneratorService>();
builder.Services.AddScoped<QuestionGeneratorService>();

var app = builder.Build();

// Auto-create DB
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

app.Run();
