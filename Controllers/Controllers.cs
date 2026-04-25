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
        _db = db;
        _generator = generator;
        _config = config;
        _logger = logger;
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

        var session = new QuizSession
        {
            UserId = CurrentUserId.Value,
            Topics = model.Topics,
            Status = "pending_payment"
        };

        _db.QuizSessions.Add(session);
        await _db.SaveChangesAsync();

        // Build Meshulam payment URL
        // After payment, Meshulam redirects to success_url with transaction params
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var successUrl = Uri.EscapeDataString($"{baseUrl}/Quiz/PaymentSuccess?sessionId={session.Id}");
        var meshulam = _config["Payment:Url"];

        if (!string.IsNullOrEmpty(meshulam))
        {
            var paymentLink = $"{meshulam}&sum=49&description=QuizGen-{session.Id}&success_url={successUrl}";
            return Redirect(paymentLink);
        }

        // No payment URL — demo mode
        return RedirectToAction("SimulatePayment", new { id = session.Id });
    }

    // Called by Meshulam after successful payment (redirect back to site)
    [HttpGet]
    public async Task<IActionResult> PaymentSuccess(int sessionId, string? transaction_id)
    {
        var session = await _db.QuizSessions.FindAsync(sessionId);
        if (session == null) return NotFound();

        // Verify session belongs to logged-in user (or trust Meshulam redirect)
        if (CurrentUserId != null && session.UserId != CurrentUserId.Value)
            return Forbid();

        if (session.Status == "pending_payment")
        {
            session.Status = "generating";
            session.PaidAt = DateTime.UtcNow;
            session.AnswersAvailableAt = DateTime.UtcNow.AddHours(48);
            session.PaymentReference = transaction_id ?? $"MSH-{session.Id}";
            await _db.SaveChangesAsync();

            // Generate questions in background
            _ = GenerateInBackground(session.Id, session.Topics);
        }

        return RedirectToAction("Generating", new { id = session.Id });
    }

    // Meshulam server-to-server webhook (POST)
    [HttpPost]
    public async Task<IActionResult> PaymentWebhook()
    {
        try
        {
            var form = await Request.ReadFormAsync();
            var description = form["description"].ToString(); // "QuizGen-{id}"
            var status = form["status"].ToString();
            var transactionId = form["transaction_id"].ToString();

            if (status != "1") return Ok("not_success");

            if (!description.StartsWith("QuizGen-")) return BadRequest("unknown");
            if (!int.TryParse(description.Replace("QuizGen-", ""), out var sessionId))
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

                _ = GenerateInBackground(session.Id, session.Topics);
            }

            return Ok("ok");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Webhook error");
            return StatusCode(500);
        }
    }

    private async Task GenerateInBackground(int sessionId, string topics)
    {
        try
        {
            var questions = await _generator.GenerateQuestionsAsync(topics, sessionId, 5);

            // Use a fresh scope for DB access in background thread
            var scope = HttpContext.RequestServices.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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
            _logger.LogError(ex, "Background generation failed for session {Id}", sessionId);
        }
    }

    // Demo-only: simulate payment
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

        _ = GenerateInBackground(session.Id, session.Topics);

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
            EasyQuestions = session.Questions.Where(q => q.Difficulty == "easy").ToList(),
            MediumQuestions = session.Questions.Where(q => q.Difficulty == "medium").ToList(),
            HardQuestions = session.Questions.Where(q => q.Difficulty == "hard").ToList(),
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
