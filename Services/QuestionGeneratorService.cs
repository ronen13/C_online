using System.Text;
using System.Text.Json;
using QuizSystem.Models;

namespace QuizSystem.Services;

public class QuestionGeneratorService
{
    private readonly IConfiguration _config;
    private readonly ILogger<QuestionGeneratorService> _logger;
    private readonly HttpClient _httpClient;

    // Total 100 questions: 34 easy + 33 medium + 33 hard
    private const int EasyCount   = 34;
    private const int MediumCount = 33;
    private const int HardCount   = 33;

    // API limit per single call — split into batches to avoid token limits
    private const int BatchSize = 10;

    public QuestionGeneratorService(IConfiguration config, ILogger<QuestionGeneratorService> logger, HttpClient httpClient)
    {
        _config = config;
        _logger = logger;
        _httpClient = httpClient;
    }

    public async Task<List<Question>> GenerateQuestionsAsync(string topics, int sessionId, int countPerDifficulty = 5)
    {
        // countPerDifficulty param ignored — we use fixed 100-question split
        var all = new List<Question>();
        var topicList = topics.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var plan = new[]
        {
            ("easy",   EasyCount),
            ("medium", MediumCount),
            ("hard",   HardCount)
        };

        foreach (var (diff, total) in plan)
        {
            var generated = await GenerateForDifficulty(topicList, diff, total, sessionId);
            all.AddRange(generated);
            _logger.LogInformation("[Quiz] Session {Id}: {Count} {Diff} questions ready", sessionId, generated.Count, diff);
        }

        return all;
    }

    private async Task<List<Question>> GenerateForDifficulty(string[] topics, string difficulty, int total, int sessionId)
    {
        var apiKey = _config["Claude:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("API key missing — using mock questions");
            return GenerateMockQuestions(topics, difficulty, sessionId, total);
        }

        var result = new List<Question>();
        int batches = (int)Math.Ceiling((double)total / BatchSize);

        for (int b = 0; b < batches; b++)
        {
            int batchCount = (b == batches - 1) ? total - result.Count : BatchSize;
            int attempt = 0;

            while (attempt < 3 && batchCount > 0)
            {
                try
                {
                    var prompt = BuildPrompt(topics, difficulty, batchCount, b + 1, batches);
                    var batch = await CallApiAsync(prompt, topics, difficulty, sessionId);
                    result.AddRange(batch);
                    break;
                }
                catch (Exception ex)
                {
                    attempt++;
                    _logger.LogWarning(ex, "Batch {B} attempt {A} failed for {Diff}", b, attempt, difficulty);
                    await Task.Delay(1500 * attempt);
                }
            }

            // Small delay between batches to respect rate limits
            if (b < batches - 1) await Task.Delay(500);
        }

        // Fallback if API returned fewer than expected
        if (result.Count < total)
        {
            var missing = total - result.Count;
            result.AddRange(GenerateMockQuestions(topics, difficulty, sessionId, missing));
        }

        return result;
    }

    private string BuildPrompt(string[] topics, string difficulty, int count, int batch, int totalBatches)
    {
        var diffHe = difficulty switch
        {
            "easy"   => "קלות (מושגים בסיסיים ב-C#, מתאים למתחילים)",
            "medium" => "בינוניות (הבנה ויישום של C#, דורשות חשיבה)",
            "hard"   => "קשות (ניתוח, best practices, edge cases ב-C#)",
            _        => difficulty
        };

        return $@"אתה מומחה C# ו-.NET. צור בדיוק {count} שאלות אמריקאיות {diffHe} על: {string.Join(", ", topics)}.
זוהי קבוצה {batch} מתוך {totalBatches} — אל תחזור על שאלות מקבוצות קודמות.

החזר JSON בלבד, ללא markdown, ללא טקסט נוסף:
{{
  ""questions"": [
    {{
      ""topic"": ""שם הנושא הספציפי"",
      ""text"": ""טקסט השאלה (כולל קטע קוד אם רלוונטי)"",
      ""optionA"": ""..."",
      ""optionB"": ""..."",
      ""optionC"": ""..."",
      ""optionD"": ""..."",
      ""correctAnswer"": ""A|B|C|D"",
      ""explanation"": ""הסבר מפורט מדוע זו התשובה הנכונה""
    }}
  ]
}}

כללים:
- עברית מלאה (מלבד מונחי קוד: null, async, IEnumerable וכו')
- הכנס snippets קצרים של C# בשאלות כשרלוונטי
- פזר את התשובות הנכונות בין A/B/C/D באופן שווה
- כל שאלה על נושא שונה מהקבוצה";
    }

    private async Task<List<Question>> CallApiAsync(string prompt, string[] topics, string difficulty, int sessionId)
    {
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("x-api-key", _config["Claude:ApiKey"]);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var body = new
        {
            model = "claude-opus-4-5",
            max_tokens = 4096,
            messages = new[] { new { role = "user", content = prompt } }
        };

        var response = await _httpClient.PostAsync(
            "https://api.anthropic.com/v1/messages",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        );

        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        var parsed = JsonDocument.Parse(text);
        var content = parsed.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

        var start = content.IndexOf('{');
        var end   = content.LastIndexOf('}') + 1;
        if (start < 0 || end <= start) throw new Exception("No JSON in response");

        var result = JsonDocument.Parse(content[start..end]);
        var list   = new List<Question>();

        foreach (var q in result.RootElement.GetProperty("questions").EnumerateArray())
        {
            list.Add(new Question
            {
                SessionId     = sessionId,
                Topic         = q.GetProperty("topic").GetString() ?? topics[0],
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

        return list;
    }

    private List<Question> GenerateMockQuestions(string[] topics, string difficulty, int sessionId, int count)
    {
        var samples = difficulty switch
        {
            "easy" => new[]
            {
                ("יסודות C#",    "מה המילה השמורה ליצירת מחלקה?",                                  "class","struct","object","interface","A","ב-C# מגדירים מחלאה עם class. struct הוא סוג-ערך."),
                ("יסודות C#",    "מה הפלט של Console.WriteLine(10 / 3)?",                          "3","3.33","3.0","שגיאה","A","חלוקת int ב-int מחזירה int — החלק השלם."),
                ("טיפוסים",      "מה הגודל של int ב-C#?",                                          "32 bit","16 bit","64 bit","תלוי במערכת","A","int הוא System.Int32 — תמיד 32 ביט ב-C#."),
                ("מחרוזות",      "איך בודקים אם string ריק?",                                      "string.IsNullOrEmpty(s)","s == null","s.Length","s.Empty","A","IsNullOrEmpty בודק גם null וגם ריק."),
                ("OOP",          "מה Encapsulation?",                                               "הסתרת פרטים פנימיים","ירושה","polymorphism","interface","A","הסתרת המימוש וחשיפת API בלבד."),
                ("לולאות",       "מה ההבדל בין for ל-foreach?",                                    "foreach מיועד לאוספים","for מהיר יותר תמיד","אין הבדל","foreach לא עובד על מערכים","A","foreach מיועד לריצה על IEnumerable."),
                ("Collections",  "מה ההבדל בין List<T> למערך?",                                   "List גמיש בגודל","מערך מהיר יותר תמיד","אין הבדל","List לא ניתן להרחבה","A","List<T> מגדיל את עצמו דינמית."),
                ("Nullable",     "מה הסימון לטיפוס nullable?",                                     "int?","int!","null<int>","nullable int","A","int? = Nullable<int> — מאפשר null."),
                ("Exception",    "מה יוצר try-catch?",                                             "תפיסת שגיאות","לולאה","interface","thread","A","try-catch תופס exceptions בזמן ריצה."),
                ("const",        "מה ההבדל בין const ל-readonly?",                                 "const בזמן קומפילציה, readonly בזמן ריצה","אין הבדל","readonly מהיר יותר","const ניתן לשינוי","A","const חייב להיות ידוע בקומפילציה.")
            },
            "medium" => new[]
            {
                ("LINQ",         "מה ההבדל בין Where ל-First?",                                    "Where מחזיר IEnumerable, First מחזיר אלמנט","אין הבדל","First מסנן","Where מחזיר null","A","First זורק exception אם ריק."),
                ("async/await",  "מה הבעיה ב-async void?",                                        "Exceptions לא נתפסות","לא יקמפל","חוסם UI","לא מאפשר await","A","יש להשתמש ב-async Task."),
                ("OOP",          "מה abstract class לעומת interface?",                             "Abstract יכול להכיל מימוש","אין הבדל","Interface יכול להכיל שדות","Abstract לא ניתן לירושה","A","Interface (לפני C#8) רק חתימות."),
                ("Generics",     "מה where T : class מגביל?",                                     "T חייב להיות reference type","T חייב להיות struct","T חייב להכיל constructor","T חייב להיות public","A","מגביל ל-reference types בלבד."),
                ("Delegates",    "מה Func<int, string>?",                                          "Delegate שמקבל int ומחזיר string","Delegate ללא פרמטרים","Action עם string","Generic method","A","הפרמטר האחרון ב-Func הוא סוג ההחזרה."),
                ("IEnumerable",  "מה yield return עושה?",                                         "מחזיר ערך ומשהה","יוצא מהפונקציה","זורק exception","מחזיר null","A","יוצר iterator — הפונקציה ממשיכה בקריאה הבאה."),
                ("EF Core",      "מה ההבדל בין Add ל-Attach?",                                    "Add=Added, Attach=Unchanged","אין הבדל","Attach זורק exception","Add מבצע INSERT מיד","A","Attach לא יייצר INSERT אלא אם תשנה מאפיין."),
                ("Extensions",   "מה Extension Method?",                                           "מתודה שנוספת לטיפוס קיים","מתודה סטטית פרטית","override של מתודה","virtual method","A","מוגדר כ-static עם this כפרמטר ראשון."),
                ("Pattern",      "מה switch expression ב-C# 8?",                                  "ביטוי switch שמחזיר ערך","לולאת switch","switch עם regex","switch לטיפוסים בלבד","A","תחביר קומפקטי: x switch { pattern => result }."),
                ("Tasks",        "מה Task.WhenAll עושה?",                                         "ממתין לכל ה-Tasks במקביל","מריץ Tasks ברצף","מבטל Tasks","מחזיר את הראשון","A","כל ה-Tasks רצים במקביל, ממתין עד שכולם מסיימים.")
            },
            _ => new[]
            {
                ("async",        "מה ConfigureAwait(false) עושה?",                                "ממשיך ב-thread שרירותי","מבטל await","מוסיף timeout","חוזר ל-UI thread","A","מונע חזרה ל-SynchronizationContext המקורי — חשוב בספריות."),
                ("Memory",       "מה Span<T>?",                                                   "מבט על זיכרון רציף ללא הקצאה","Generic collection","pointer ל-unmanaged","thread-safe buffer","A","Span<T> על ה-stack — אפס allocations."),
                ("Concurrency",  "מה Double-checked locking?",                                    "בדיקת null לפני ואחרי lock","נעילה כפולה","async lock","anti-pattern","A","מונע overhead של lock כשהסינגלטון אותחל."),
                ("IQueryable",   "מה ההבדל בין IQueryable ל-IEnumerable ב-DB?",                  "IQueryable מתרגם ל-SQL","IEnumerable מהיר יותר","אין הבדל","IQueryable עובד בזיכרון","A","IQueryable מסנן בשרת, IEnumerable מביא הכל לזיכרון."),
                ("Contravariance","מה in T ב-Generics?",                                          "Contravariance — מאפשר שימוש ב-base type","Covariance","sealed generic","readonly generic","A","Action<Base> ניתן להשמה ל-Action<Derived>."),
                ("Dispose",      "מה IAsyncDisposable?",                                          "async cleanup עם await using","sync dispose","thread-safe dispose","cancel disposal","A","await using מאפשר ניקוי משאבים async."),
                ("Records",      "מה record ב-C# 9?",                                             "immutable reference type עם value equality","mutable class","struct מיוחד","interface","A","Records משווים לפי ערכים, לא reference."),
                ("Source Gen",   "מה Source Generators ב-C#?",                                   "קוד שנוצר בזמן קומפילציה","runtime code gen","reflection","AOP","A","מייצרים קוד C# אוטומטית בזמן build ללא runtime overhead."),
                ("Channels",     "מה System.Threading.Channels?",                                 "תור producer-consumer thread-safe","lock wrapper","async mutex","event aggregator","A","מחליף תבניות BlockingCollection בעולם async."),
                ("Expressions",  "מה Expression<Func<T>> לעומת Func<T>?",                       "Expression שומר את עץ הביטוי לניתוח","אין הבדל","Expression מהיר יותר","Expression לא ניתן להרצה","A","LINQ to SQL משתמש בזה לתרגום ל-SQL.")
            }
        };

        var result = new List<Question>();
        for (int i = 0; i < count; i++)
        {
            var s = samples[i % samples.Length];
            var topicName = s.Item1.Length > 0 ? s.Item1 : topics[i % topics.Length];
            result.Add(new Question
            {
                SessionId = sessionId, Topic = topicName, Difficulty = difficulty,
                Text = s.Item2, OptionA = s.Item3, OptionB = s.Item4,
                OptionC = s.Item5, OptionD = s.Item6,
                CorrectAnswer = s.Item7, Explanation = s.Item8
            });
        }
        return result;
    }
}
