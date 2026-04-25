namespace QuizSystem.Models;

public class User
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<QuizSession> Sessions { get; set; } = new();
}

public class QuizSession
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string Topics { get; set; } = "";
    public string Status { get; set; } = "pending_payment";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PaidAt { get; set; }
    public DateTime? AnswersAvailableAt { get; set; }
    public string? PaymentReference { get; set; }
    public List<Question> Questions { get; set; } = new();
}

public class Question
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public QuizSession Session { get; set; } = null!;
    public string Topic { get; set; } = "";
    public string Difficulty { get; set; } = "";
    public string Text { get; set; } = "";
    public string OptionA { get; set; } = "";
    public string OptionB { get; set; } = "";
    public string OptionC { get; set; } = "";
    public string OptionD { get; set; } = "";
    public string CorrectAnswer { get; set; } = "";
    public string Explanation { get; set; } = "";
}

public class RegisterViewModel
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

public class LoginViewModel
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

public class CreateQuizViewModel
{
    public List<string> SelectedTopics { get; set; } = new();
}

public class QuizResultViewModel
{
    public QuizSession Session { get; set; } = null!;
    public List<Question> EasyQuestions { get; set; } = new();
    public List<Question> MediumQuestions { get; set; } = new();
    public List<Question> HardQuestions { get; set; } = new();
    public bool AnswersAvailable { get; set; }
    public TimeSpan? TimeUntilAnswers { get; set; }
}
