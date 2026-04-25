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
            var generated = await CallClaudeApiAsync(prompt, topics, difficulty, sessionId);
            questions.AddRange(generated);
        }

        return questions;
    }

    private string BuildPrompt(string[] topics, string difficulty, int count)
    {
        var difficultyHe = difficulty switch
        {
            "easy" => "קלות (מתאים למתחילים, מושגים בסיסיים)",
            "medium" => "בינוניות (מצריכות הבנה ויישום)",
            "hard" => "קשות (מצריכות ניתוח עמוק וחשיבה ביקורתית)",
            _ => difficulty
        };

        return $@"צור {count} שאלות {difficultyHe} על הנושאים הבאים: {string.Join(", ", topics)}.

החזר תשובה בפורמט JSON בלבד (ללא טקסט נוסף):
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
      ""explanation"": ""הסבר מפורט על התשובה הנכונה""
    }}
  ]
}}

חשוב:
- כתוב הכל בעברית
- התשובות הנכונות יהיו מגוונות (לא תמיד A)
- ההסבר יהיה ברור ומועיל
- הפחד לא לחזור על שאלות דומות";
    }

    private async Task<List<Question>> CallClaudeApiAsync(string prompt, string topics, string difficulty, int sessionId)
    {
        var questions = new List<Question>();

        try
        {
            var apiKey = _config["Claude:ApiKey"];
            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.LogWarning("Claude API key not configured, using mock data");
                return GenerateMockQuestions(topics, difficulty, sessionId, 5);
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
            var content = parsed.RootElement
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString() ?? "";

            var jsonStart = content.IndexOf('{');
            var jsonEnd = content.LastIndexOf('}') + 1;
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonStr = content[jsonStart..jsonEnd];
                var result = JsonDocument.Parse(jsonStr);
                var questionsArr = result.RootElement.GetProperty("questions");

                foreach (var q in questionsArr.EnumerateArray())
                {
                    questions.Add(new Question
                    {
                        SessionId = sessionId,
                        Topic = q.GetProperty("topic").GetString() ?? topics,
                        Difficulty = difficulty,
                        Text = q.GetProperty("text").GetString() ?? "",
                        OptionA = q.GetProperty("optionA").GetString() ?? "",
                        OptionB = q.GetProperty("optionB").GetString() ?? "",
                        OptionC = q.GetProperty("optionC").GetString() ?? "",
                        OptionD = q.GetProperty("optionD").GetString() ?? "",
                        CorrectAnswer = q.GetProperty("correctAnswer").GetString() ?? "A",
                        Explanation = q.GetProperty("explanation").GetString() ?? ""
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Claude API, falling back to mock data");
            return GenerateMockQuestions(topics, difficulty, sessionId, 5);
        }

        return questions;
    }

    private List<Question> GenerateMockQuestions(string topics, string difficulty, int sessionId, int count)
    {
        var questions = new List<Question>();
        var topicList = topics.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var diffLabels = new Dictionary<string, string>
        {
            ["easy"] = "קלה",
            ["medium"] = "בינונית",
            ["hard"] = "קשה"
        };

        for (int i = 1; i <= count; i++)
        {
            var topic = topicList[(i - 1) % topicList.Length];
            var answers = new[] { "A", "B", "C", "D" };
            var correct = answers[i % 4];

            questions.Add(new Question
            {
                SessionId = sessionId,
                Topic = topic,
                Difficulty = difficulty,
                Text = $"שאלה {diffLabels[difficulty]} מספר {i} על הנושא: {topic}",
                OptionA = $"תשובה א' לשאלה {i}",
                OptionB = $"תשובה ב' לשאלה {i}",
                OptionC = $"תשובה ג' לשאלה {i}",
                OptionD = $"תשובה ד' לשאלה {i}",
                CorrectAnswer = correct,
                Explanation = $"הסבר לשאלה {i}: התשובה הנכונה היא {correct} כי זוהי השאלה הנכונה ביותר עבור הנושא {topic}."
            });
        }

        return questions;
    }
}
