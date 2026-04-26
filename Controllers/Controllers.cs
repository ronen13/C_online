using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuizSystem.Data;
using QuizSystem.Models;
using QuizSystem.Services;

namespace QuizSystem.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => View();
}

public class AuthController : Controller
{
    private readonly AppDbContext _db;
    public AuthController(AppDbContext db) => _db = db;

    [HttpGet] public IActionResult Register() => View();
    [HttpGet] public IActionResult Login() => View();

    [HttpPost]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        if (await _db.Users.AnyAsync(u => u.Email == model.Email))
        {
            ViewBag.Error = "כתובת האימייל כבר קיימת במערכת";
            return View(model);
        }
        var user = new User
        {
            Name = model.Name,
            Email = model.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(model.Password)
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        HttpContext.Session.SetInt32("UserId", user.Id);
        HttpContext.Session.SetString("UserName", user.Name);
        return RedirectToAction("Create", "Quiz");
    }

    [HttpPost]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == model.Email);
        if (user == null || !BCrypt.Net.BCrypt.Verify(model.Password, user.PasswordHash))
        {
            ViewBag.Error = "אימייל או סיסמה שגויים";
            return View(model);
        }
        HttpContext.Session.SetInt32("UserId", user.Id);
        HttpContext.Session.SetString("UserName", user.Name);
        return RedirectToAction("MySessions", "Quiz");
    }

    public IActionResult Logout()
    {
        HttpContext.Session.Clear();
        return RedirectToAction("Index", "Home");
    }
}

public class QuizController : Controller
{
    private readonly AppDbContext _db;
    private readonly QuestionGeneratorService _generator;
    private readonly IConfiguration _config;
    private readonly ILogger<QuizController> _logger;

    public QuizController(AppDbContext db, QuestionGeneratorService generator,
        IConfiguration config, ILogger<QuizController> logger)
    {
        _db = db; _generator = generator; _config = config; _logger = logger;
    }

    private int? CurrentUserId => HttpContext.Session.GetInt32("UserId");

    [HttpGet]
    public IActionResult Create()
    {
        if (CurrentUserId == null) return RedirectToAction("Login", "Auth");
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateQuizViewModel model)
    {
        if (CurrentUserId == null) return RedirectToAction("Login", "Auth");
        if (model.SelectedTopics == null || !model.SelectedTopics.Any())
        {
            ViewBag.Error = "יש לבחור לפחות נושא אחד";
            return View(model);
        }

        var topics = string.Join(", ", model.SelectedTopics);
        var session = new QuizSession
        {
            UserId = CurrentUserId.Value,
            Topics = topics,
            Status = "pending_payment"
        };
        _db.QuizSessions.Add(session);
        await _db.SaveChangesAsync();

        var baseUrl = _config["App:BaseUrl"] ?? $"{Request.Scheme}://{Request.Host}";
        var successUrl = Uri.EscapeDataString($"{baseUrl}/Quiz/PaymentSuccess?sessionId={session.Id}");
        var meshulam = _config["Payment:Url"];

        if (!string.IsNullOrEmpty(meshulam))
        {
            var paymentLink = $"{meshulam}&sum=99&description=MarketClab-Quiz-{session.Id}&success_url={successUrl}";
            return Redirect(paymentLink);
        }

        return RedirectToAction("SimulatePayment", new { id = session.Id });
    }

    // ← זהו הקישור שמכניסים במשולם כ-Callback URL
    [HttpGet]
    public async Task<IActionResult> PaymentSuccess(int sessionId, string? transaction_id)
    {
        var session = await _db.QuizSessions.FindAsync(sessionId);
        if (session == null) return NotFound();

        if (session.Status == "pending_payment")
        {
            session.Status = "generating";
            session.PaidAt = DateTime.UtcNow;
            session.AnswersAvailableAt = DateTime.UtcNow.AddHours(48);
            session.PaymentReference = transaction_id ?? $"MSH-{session.Id}";
            await _db.SaveChangesAsync();
            _ = GenerateInBackground(session.Id, session.Topics, HttpContext.RequestServices);
        }

        // אם המשתמש לא מחובר, שמור session ID ב-cookie כדי שיוכל לראות אחרי login
        if (CurrentUserId == null)
        {
            HttpContext.Session.SetInt32("PendingSession", sessionId);
            return RedirectToAction("Login", "Auth");
        }

        return RedirectToAction("Generating", new { id = session.Id });
    }

    // Server-to-server webhook מ-Meshulam
    [HttpPost]
    public async Task<IActionResult> PaymentWebhook()
    {
        try
        {
            var form = await Request.ReadFormAsync();
            var description = form["description"].ToString();
            var status = form["status"].ToString();
            var transactionId = form["transaction_id"].ToString();

            if (status != "1") return Ok("not_success");
            if (!description.StartsWith("MarketClab-Quiz-")) return BadRequest("unknown");
            if (!int.TryParse(description.Replace("MarketClab-Quiz-", ""), out var sessionId))
                return BadRequest("bad_id");

            var session = await _db.QuizSessions.FindAsync(sessionId);
            if (session == null) return NotFound();

            if (session.Status == "pending_payment")
            {
                session.Status = "generating";
                session.PaidAt = DateTime.UtcNow;
                session.AnswersAvailableAt = DateTime.UtcNow.AddHours(48);
                session.PaymentReference = transactionId;
                await _db.SaveChangesAsync();
                _ = GenerateInBackground(session.Id, session.Topics, HttpContext.RequestServices);
            }

            return Ok("ok");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Webhook error");
            return StatusCode(500);
        }
    }

    private static async Task GenerateInBackground(int sessionId, string topics, IServiceProvider services)
    {
        await Task.Delay(500);
        try
        {
            using var scope = services.CreateScope();
            var generator = scope.ServiceProvider.GetRequiredService<QuestionGeneratorService>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var questions = await generator.GenerateQuestionsAsync(topics, sessionId, 5);

            var s = await db.QuizSessions.FindAsync(sessionId);
            if (s != null)
            {
                db.Questions.AddRange(questions);
                s.Status = "ready";
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            // Log but don't crash
            Console.WriteLine($"[GenerateBackground] Error for session {sessionId}: {ex.Message}");
        }
    }

    public async Task<IActionResult> SimulatePayment(int id)
    {
        if (CurrentUserId == null) return RedirectToAction("Login", "Auth");
        var session = await _db.QuizSessions.FindAsync(id);
        if (session == null || session.UserId != CurrentUserId.Value) return NotFound();
        ViewBag.Session = session;
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> ConfirmPayment(int id)
    {
        if (CurrentUserId == null) return RedirectToAction("Login", "Auth");
        var session = await _db.QuizSessions.FindAsync(id);
        if (session == null || session.UserId != CurrentUserId.Value) return NotFound();

        session.Status = "generating";
        session.PaidAt = DateTime.UtcNow;
        session.AnswersAvailableAt = DateTime.UtcNow.AddHours(48);
        session.PaymentReference = $"DEMO-{Guid.NewGuid().ToString()[..8].ToUpper()}";
        await _db.SaveChangesAsync();

        _ = GenerateInBackground(session.Id, session.Topics, HttpContext.RequestServices);
        return RedirectToAction("Generating", new { id = session.Id });
    }

    public async Task<IActionResult> Generating(int id)
    {
        if (CurrentUserId == null) return RedirectToAction("Login", "Auth");
        var session = await _db.QuizSessions.FindAsync(id);
        if (session == null || session.UserId != CurrentUserId.Value) return NotFound();
        return View(session);
    }

    public async Task<IActionResult> View(int id)
    {
        if (CurrentUserId == null) return RedirectToAction("Login", "Auth");
        var session = await _db.QuizSessions
            .Include(s => s.Questions)
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == CurrentUserId.Value);
        if (session == null) return NotFound();

        var now = DateTime.UtcNow;
        var answersAvailable = session.AnswersAvailableAt.HasValue && now >= session.AnswersAvailableAt.Value;

        var vm = new QuizResultViewModel
        {
            Session = session,
            EasyQuestions   = session.Questions.Where(q => q.Difficulty == "easy").ToList(),
            MediumQuestions = session.Questions.Where(q => q.Difficulty == "medium").ToList(),
            HardQuestions   = session.Questions.Where(q => q.Difficulty == "hard").ToList(),
            AnswersAvailable = answersAvailable,
            TimeUntilAnswers = answersAvailable ? null : session.AnswersAvailableAt - now
        };
        return View(vm);
    }

    public async Task<IActionResult> MySessions()
    {
        if (CurrentUserId == null) return RedirectToAction("Login", "Auth");
        var sessions = await _db.QuizSessions
            .Where(s => s.UserId == CurrentUserId.Value)
            .Include(s => s.Questions)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
        return View(sessions);
    }

    [HttpGet]
    public async Task<IActionResult> Status(int id)
    {
        var session = await _db.QuizSessions.FindAsync(id);
        if (session == null) return NotFound();
        return Json(new { status = session.Status });
    }
}
