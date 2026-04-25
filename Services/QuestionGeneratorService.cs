using System.Text;
using System.Text.Json;
using QuizSystem.Models;

namespace QuizSystem.Services;

public class QuestionGeneratorService
{
    private readonly IConfiguration _config;
    private readonly ILogger<QuestionGeneratorService> _logger;
    private readonly HttpClient _httpClient;

    public QuestionGeneratorService(IConfiguration config, ILogger<QuestionGeneratorService> logger, HttpClient httpClient)
    {
        _config = config;
        _logger = logger;
        _httpClient = httpClient;
    }

    public async Task<List<Question>> GenerateQuestionsAsync(string topics, int sessionId, int countPerDifficulty = 5)
    {
        var questions = new List<Question>();
        var topicList = topics.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var difficulties = new[] { "easy", "medium", "hard" };

        foreach (var difficulty in difficulties)
        {
            var prompt = BuildPrompt(topicList, difficulty, countPerDifficulty);
            var generated = await CallApiAsync(prompt, topics, difficulty, sessionId, countPerDifficulty);
            questions.AddRange(generated);
        }

        return questions;
    }

    private string BuildPrompt(string[] topics, string difficulty, int count)
    {
        var difficultyHe = difficulty switch
        {
            "easy"   => "קלות (מתאים למתחילים, מושגים בסיסיים ב-C#)",
            "medium" => "בינוניות (מצריכות הבנה ויישום של C#)",
            "hard"   => "קשות (מצריכות ניתוח עמוק, חשיבה ביקורתית ו-best practices)",
            _        => difficulty
        };

        return $@"אתה מומחה ב-C# ו-.NET. צור {count} שאלות אמריקאיות {difficultyHe} על הנושאים הבאים: {string.Join(", ", topics)}.

החזר תשובה בפורמט JSON בלבד (ללא טקסט נוסף, ללא markdown):
{{
  ""questions"": [
    {{
      ""topic"": ""שם הנושא"",
      ""text"": ""טקסט השאלה"",
      ""optionA"": ""אפשרות א'"",
      ""optionB"": ""אפשרות ב'"",
      ""optionC"": ""אפשרות ג'"",
      ""optionD"": ""אפשרות ד'"",
      ""correctAnswer"": ""A"",
      ""explanation"": ""הסבר מפורט על התשובה הנכונה ועל העיקרון ב-C#""
    }}
  ]
}}

חוקים:
- כתוב הכל בעברית (מלבד מונחי קוד כגון: null, async, IEnumerable וכו')
- הכנס קטעי קוד C# קצרים בשאלות כשרלוונטי
- התשובות הנכונות יהיו מגוונות (לא תמיד A)
- אל תחזור על שאלות דומות
- ההסבר יסביר את העיקרון הנכון ב-C#";
    }

    private async Task<List<Question>> CallApiAsync(string prompt, string topics, string difficulty, int sessionId, int count)
    {
        var questions = new List<Question>();
        try
        {
            var apiKey = _config["Claude:ApiKey"];
            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.LogWarning("API key not configured, using mock data");
                return GenerateMockQuestions(topics, difficulty, sessionId, count);
            }

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
            _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

            var requestBody = new
            {
                model = "claude-opus-4-5",
                max_tokens = 4000,
                messages = new[] { new { role = "user", content = prompt } }
            };

            var response = await _httpClient.PostAsync(
                "https://api.anthropic.com/v1/messages",
                new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            );

            var responseText = await response.Content.ReadAsStringAsync();
            var parsed = JsonDocument.Parse(responseText);
            var content = parsed.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

            var jsonStart = content.IndexOf('{');
            var jsonEnd   = content.LastIndexOf('}') + 1;
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var result = JsonDocument.Parse(content[jsonStart..jsonEnd]);
                foreach (var q in result.RootElement.GetProperty("questions").EnumerateArray())
                {
                    questions.Add(new Question
                    {
                        SessionId     = sessionId,
                        Topic         = q.GetProperty("topic").GetString() ?? topics,
                        Difficulty    = difficulty,
                        Text          = q.GetProperty("text").GetString() ?? "",
                        OptionA       = q.GetProperty("optionA").GetString() ?? "",
                        OptionB       = q.GetProperty("optionB").GetString() ?? "",
                        OptionC       = q.GetProperty("optionC").GetString() ?? "",
                        OptionD       = q.GetProperty("optionD").GetString() ?? "",
                        CorrectAnswer = q.GetProperty("correctAnswer").GetString() ?? "A",
                        Explanation   = q.GetProperty("explanation").GetString() ?? ""
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating questions, using mock data");
            return GenerateMockQuestions(topics, difficulty, sessionId, count);
        }
        return questions;
    }

    private List<Question> GenerateMockQuestions(string topics, string difficulty, int sessionId, int count)
    {
        var questions = new List<Question>();
        var topicList = topics.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Real C# sample questions per difficulty
        var samples = difficulty switch
        {
            "easy" => new[]
            {
                ("מהי המילה השמורה להגדרת מחלקה ב-C#?", "class", "struct", "object", "type", "A", "ב-C# משתמשים במילה `class` להגדרת מחלאה. `struct` משמש לסוגי ערך, `object` הוא מחלקת הבסיס."),
                ("מה יודפס? `Console.WriteLine(10 / 3);`", "3", "3.33", "3.0", "שגיאה", "A", "חלוקת int ב-int ב-C# מחזירה int — החלק השלם בלבד. לקבלת 3.33 יש לכתוב `10.0 / 3`."),
                ("איזו מילה שמורה מגדירה קבוע ב-C#?", "const", "readonly", "fixed", "static", "A", "`const` מגדיר ערך קבוע בזמן קומפילציה. `readonly` מאפשר השמה פעם אחת בזמן ריצה."),
                ("מה הטיפוס המוחזר של `bool` ב-C#?", "true/false", "0/1", "yes/no", "on/off", "A", "`bool` מחזיר `true` או `false` בלבד."),
                ("כיצד מגדירים מערך של int ב-C#?", "int[] arr", "int arr[]", "array<int>", "List<int>", "A", "התחביר הנכון ב-C# הוא `int[] arr = new int[5];`.")
            },
            "medium" => new[]
            {
                ("מה ההבדל בין `IEnumerable<T>` ל-`IList<T>`?", "IList מאפשר גישה לפי אינדקס", "IEnumerable מהיר יותר", "IList לקריאה בלבד", "אין הבדל", "A", "`IList<T>` מרחיב את `ICollection<T>` ומאפשר גישה לפי אינדקס (`list[0]`). `IEnumerable<T>` מאפשר ריצה קדימה בלבד."),
                ("מה עושה `yield return` ב-C#?", "מחזיר ערך בלי לסיים את המתודה", "זורק exception", "יוצא מהלולאה", "מחזיר null", "A", "`yield return` יוצר iterator — הפונקציה מחזירה ערך ומשהה את עצמה עד הקריאה הבאה."),
                ("מה יקרה אם תקרא למתודה `async` בלי `await`?", "תחזיר Task ללא המתנה", "תזרוק exception", "תחסום את ה-thread", "לא תקמפל", "A", "קריאה ל-`async` בלי `await` תחזיר `Task` מיידית — הקוד ירוץ ברקע ללא המתנה לתוצאה."),
                ("מה ההבדל בין `==` ל-`Equals()` ב-C#?", "`==` משווה reference, `Equals` משווה ערך (לרוב)", "`Equals` מהיר יותר", "אין הבדל", "`==` לא עובד על objects", "A", "עבור `string`, `==` עובד על ערכים. עבור מחלקות מותאמות אישית, `==` משווה reference אלא אם override בוצע."),
                ("מה עושה LINQ `Where`?", "מסנן אלמנטים לפי תנאי", "ממיין את האוסף", "מחזיר את הראשון", "מבצע group", "A", "`Where` מסנן את האוסף ומחזיר `IEnumerable<T>` עם האלמנטים שעומדים בתנאי.")
            },
            _ => new[]
            {
                ("מה הבעיה בקוד הבא? `async void MyMethod() { await Task.Delay(1000); }`", "לא ניתן לתפוס exceptions", "לא יקמפל", "יחסום את ה-UI", "לא תהיה בעיה", "A", "`async void` מסוכן כי exceptions שנזרקות בו לא ניתנות לתפיסה מחוץ למתודה. יש להשתמש ב-`async Task`."),
                ("מה Contravariance ב-Generics?", "מאפשר שימוש ב-base type במקום derived", "מאפשר המרה אוטומטית", "מונע boxing", "משפר ביצועים", "A", "Contravariance (עם `in`) מאפשר להשתמש ב-`Action<Base>` במקום `Action<Derived>` — הכיוון ההפוך מ-covariance."),
                ("מה הפלט? `Span<int> s = stackalloc int[3]; s[0]=1; Console.Write(s.Length);`", "3", "שגיאת קומפילציה", "0", "undefined", "A", "`stackalloc` מקצה זיכרון על ה-stack. `Span<T>` עוטף אותו. `.Length` מחזיר 3."),
                ("מהו Double-checked locking pattern?", "בדיקת תנאי פעמיים סביב lock לביצועים", "נעילה כפולה לאבטחה", "Pattern ל-async locks", "Anti-pattern תמיד", "A", "בודק תנאי לפני ואחרי `lock` — מונע overhead של lock כשהסינגלטון כבר אותחל, תוך שמירת thread safety."),
                ("מה IAsyncDisposable מאפשר?", "async cleanup ב-`await using`", "ביטול Tasks", "async constructors", "thread-safe dispose", "A", "`IAsyncDisposable` + `await using` מאפשר ניקוי משאבים async — למשל סגירת connections ב-async.")
            }
        };

        var topicArr = topicList.ToArray();
        for (int i = 0; i < Math.Min(count, samples.Length); i++)
        {
            var s = samples[i];
            questions.Add(new Question
            {
                SessionId = sessionId, Topic = topicArr[i % topicArr.Length], Difficulty = difficulty,
                Text = s.Item1, OptionA = s.Item2, OptionB = s.Item3, OptionC = s.Item4, OptionD = s.Item5,
                CorrectAnswer = s.Item6, Explanation = s.Item7
            });
        }
        return questions;
    }
}
