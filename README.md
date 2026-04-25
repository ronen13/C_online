# QuizGen AI — מערכת יצירת שאלות אוטומטית

## מה המערכת עושה?
- משתמש נרשם/מתחבר
- מכניס נושאים → מועבר לתשלום במשולם (₪49)
- לאחר תשלום: Claude AI מייצר 15 שאלות (5 קל + 5 בינוני + 5 קשה)
- השאלות מוצגות מיד — **התשובות נחשפות לאחר 48 שעות בדיוק**

---

## פריסה על Render (שלב אחר שלב)

### 1. העלה לגיטהאב
```bash
git init
git add .
git commit -m "Initial commit"
git remote add origin https://github.com/YOUR_USER/quizgen-ai.git
git push -u origin main
```

### 2. צור Web Service ב-Render
- כנס ל-https://render.com → New → Web Service
- חבר את ה-GitHub repository
- Render יזהה את ה-`render.yaml` אוטומטית

### 3. הגדר משתני סביבה ב-Render
ב-Dashboard → Environment → הוסף:

| מפתח | ערך |
|------|-----|
| `Claude__ApiKey` | המפתח שלך מ-https://console.anthropic.com |
| `Payment__Url` | `https://meshulam.co.il/quick_payment?b=94e3eb6d1865b2f42bef52f9966512df` |

### 4. הגדר Webhook במשולם
לאחר שהאתר עלה ויש לך URL (למשל `https://quizgen-ai.onrender.com`):
- כנס לחשבון משולם → הגדרות דף תשלום
- הגדר **Callback URL** (POST): `https://quizgen-ai.onrender.com/Quiz/PaymentWebhook`
- הגדר **Success URL**: `https://quizgen-ai.onrender.com/Quiz/PaymentSuccess?sessionId={id}`

---

## פלו תשלום
```
משתמש → Create Quiz → Meshulam (₪49) → PaymentSuccess (redirect)
                                       → PaymentWebhook (server POST)
                                                ↓
                                       Claude API מייצר שאלות
                                                ↓
                                       מוצג בחן, תשובות נעולות 48 שעות
```

---

## הרצה מקומית לפיתוח
```bash
# הגדר API key
export Claude__ApiKey="sk-ant-..."

dotnet run
# פתח http://localhost:5000
# ללא Payment:Url — יופעל מצב Demo (תשלום מדומה)
```

---

## מבנה הפרויקט
```
QuizSystem/
├── Controllers/Controllers.cs     # Auth + Quiz + Payment webhooks
├── Models/Models.cs               # User, QuizSession, Question
├── Services/QuestionGeneratorService.cs  # Claude API integration
├── Data/AppDbContext.cs           # SQLite via EF Core
├── Views/
│   ├── Home/Index.cshtml          # דף בית
│   ├── Auth/{Login,Register}.cshtml
│   └── Quiz/{Create,View,MySessions,Generating,SimulatePayment}.cshtml
├── Dockerfile                     # Production container
├── render.yaml                    # Render deployment config
└── appsettings.json
```

---

## טכנולוגיות
- **Backend**: ASP.NET Core 8 (C#)
- **DB**: SQLite + Entity Framework Core
- **AI**: Claude API (claude-opus-4-5)
- **תשלום**: משולם Quick Payment
- **פריסה**: Render (Docker)
- **אימות**: BCrypt + Session
