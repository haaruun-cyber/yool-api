using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using aspbackend.Data;
using aspbackend.Models;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;

namespace aspbackend.Services;

public static class Tables
{
    public const string Users = "users";
    public const string Documents = "documents";
    public const string DocumentVersions = "document_versions";
    public const string Tasks = "tasks";
    public const string Templates = "templates";
    public const string Notifications = "notifications";
    public const string Subscriptions = "subscriptions";
    public const string AnalyticsDaily = "analytics_daily";
    public const string AiHistory = "ai_history";
}

public sealed class JwtService(IConfiguration configuration)
{
    public string SignAccessToken(ObjectId userId) => Sign(userId, configuration["JWT_SECRET"] ?? "change-this-development-secret", configuration["JWT_EXPIRES_IN"] ?? "15m", false);
    public string SignRefreshToken(ObjectId userId) => Sign(userId, configuration["JWT_REFRESH_SECRET"] ?? configuration["JWT_SECRET"] ?? "change-this-development-secret", configuration["JWT_REFRESH_EXPIRES_IN"] ?? "7d", true);

    public ClaimsPrincipal ValidateRefreshToken(string token)
    {
        return new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration["JWT_REFRESH_SECRET"] ?? configuration["JWT_SECRET"] ?? "change-this-development-secret")),
            ClockSkew = TimeSpan.FromSeconds(30)
        }, out _);
    }

    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Sign(ObjectId userId, string secret, string expiresIn, bool includeJti)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, userId.ToString()) };
        if (includeJti) claims.Add(new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()));
        var token = new JwtSecurityToken(claims: claims, notBefore: now, expires: now.Add(ParseDuration(expiresIn)), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static TimeSpan ParseDuration(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return TimeSpan.FromMinutes(15);
        return double.TryParse(input[..^1], out var n) ? input[^1] switch
        {
            's' => TimeSpan.FromSeconds(n),
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => TimeSpan.FromMinutes(15)
        } : TimeSpan.FromMinutes(15);
    }
}

public sealed class EmailService(IConfiguration configuration, ILogger<EmailService> logger)
{
    public async Task<bool> SendAsync(string to, string subject, string html, string text)
    {
        var host = configuration["EMAIL_HOST"] ?? configuration["SMTP_HOST"];
        var user = configuration["EMAIL_USER"] ?? configuration["SMTP_USER"];
        var pass = configuration["EMAIL_PASS"] ?? configuration["SMTP_PASS"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user))
        {
            logger.LogWarning("[email] SMTP not configured (EMAIL_HOST / EMAIL_USER); skipping send to {To}", to);
            return false;
        }

        var port = int.TryParse(configuration["EMAIL_PORT"] ?? configuration["SMTP_PORT"], out var p) ? p : 587;
        var from = configuration["EMAIL_FROM"] ?? user;
        var secure = configuration["EMAIL_SECURE"] ?? configuration["SMTP_SECURE"];
        // Gmail and most providers: port 587 uses STARTTLS (EnableSsl=true), 465 uses SSL
        var enableSsl = port is 465 or 587 || secure is "true" or "1";

        try
        {
            using var client = new SmtpClient(host, port)
            {
                EnableSsl = enableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(user, pass),
                Timeout = 30_000
            };
            using var message = new MailMessage(from, to, subject, text) { IsBodyHtml = false };
            message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, null, "text/html"));
            await client.SendMailAsync(message);
            logger.LogInformation("[email] Sent \"{Subject}\" to {To}", subject, to);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[email] Failed to send \"{Subject}\" to {To}", subject, to);
            return false;
        }
    }
}

public sealed class CloudinaryService(IConfiguration configuration)
{
    public async Task<string> UploadAsync(IFormFile file, string folder)
    {
        var cloudName = configuration["CLOUDINARY_CLOUD_NAME"];
        var apiKey = configuration["CLOUDINARY_API_KEY"];
        var apiSecret = configuration["CLOUDINARY_API_SECRET"];
        if (string.IsNullOrWhiteSpace(cloudName) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret)) throw new InvalidOperationException("Cloudinary is not configured");
        var cloudinary = new Cloudinary(new Account(cloudName, apiKey, apiSecret));
        await using var stream = file.OpenReadStream();
        var result = await cloudinary.UploadAsync(new ImageUploadParams { File = new FileDescription(file.FileName, stream), Folder = folder });
        return result.SecureUrl?.ToString() ?? result.Url?.ToString() ?? "";
    }
}

public sealed class NotificationService(MongoDbContext db)
{
    public async Task NotifyAsync(ObjectId userId, string type, string message, BsonValue? metadata = null)
    {
        var now = DateTime.UtcNow;
        var notification = new Notification { Id = ObjectId.GenerateNewId(), UserId = userId, Type = type, Message = message, Metadata = metadata, CreatedAt = now, UpdatedAt = now };
        await db.InsertAsync(Tables.Notifications, notification.Id, notification);
    }
}

public sealed class AnalyticsService(MongoDbContext db)
{
    public Task RecordLoginAsync() => IncrementAsync(x => x.Logins++);
    public Task RecordActiveUserAsync() => IncrementAsync(x => x.ActiveUsers++);
    public Task RecordDocumentCreatedAsync() => IncrementAsync(x => x.DocumentsCreated++);

    public async Task SyncPaidUsersCountAsync()
    {
        var users = await db.AllAsync<User>(Tables.Users);
        var paid = users.LongCount(x => x.SubscriptionPlan is "pro" or "team");
        await IncrementAsync(x => x.PaidUsers = (int)paid);
    }

    private async Task IncrementAsync(Action<AnalyticsDaily> mutate)
    {
        var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var rows = await db.AllAsync<AnalyticsDaily>(Tables.AnalyticsDaily);
        var row = rows.FirstOrDefault(x => x.Date == day) ?? new AnalyticsDaily { Id = ObjectId.GenerateNewId(), Date = day };
        mutate(row);
        row.UpdatedAt = DateTime.UtcNow;
        await db.ReplaceAsync(Tables.AnalyticsDaily, row.Id, row);
    }
}

public sealed class OpenAiService(MongoDbContext db, IConfiguration configuration)
{
    public Task<string> SummarizeNote(ObjectId userId, string text) =>
        RunAsync(userId, "summarize", text, "You summarize notes into concise bullet points.");

    public Task<string> SummarizeMeeting(ObjectId userId, string text) =>
        RunAsync(userId, "meeting_summary", text, "You extract decisions, action items with owners, and open questions from meeting notes.");

    public async Task<object> GenerateTasks(ObjectId userId, string text)
    {
        var raw = await RunAsync(
            userId,
            "generate_tasks",
            text,
            "Convert the user notes into a JSON array of tasks. Each item: {\"title\": string, \"description\": string, \"priority\": \"low\"|\"medium\"|\"high\"}. Return ONLY valid JSON array.");
        try
        {
            return JsonSerializer.Deserialize<object>(raw) ?? Array.Empty<object>();
        }
        catch
        {
            return Array.Empty<object>();
        }
    }

    public Task<string> WriteAssist(ObjectId userId, string instruction, string draft) =>
        RunAsync(userId, "write", $"Instruction:\n{instruction}\n\nDraft:\n{draft}", "You are a writing assistant. Improve clarity and tone while preserving meaning.");

    private async Task<string> RunAsync(ObjectId userId, string type, string text, string instruction)
    {
        var key = configuration["OPENAI_API_KEY"];
        string output;
        if (string.IsNullOrWhiteSpace(key))
        {
            output = $"OpenAI is not configured. Instruction: {instruction}";
        }
        else
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            var payload = JsonSerializer.Serialize(new
            {
                model = configuration["OPENAI_MODEL"] ?? "gpt-4o-mini",
                messages = new object[]
                {
                    new { role = "system", content = instruction },
                    new { role = "user", content = text.Length > 8000 ? text[..8000] : text }
                },
                temperature = 0.4
            });
            var response = await http.PostAsync("https://api.openai.com/v1/chat/completions", new StringContent(payload, Encoding.UTF8, "application/json"));
            var json = await response.Content.ReadAsStringAsync();
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(json);
            output = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        }

        var history = new AiHistory { Id = ObjectId.GenerateNewId(), UserId = userId, Type = type, Prompt = text, Response = output, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        await db.InsertAsync(Tables.AiHistory, history.Id, history);
        return output;
    }
}
