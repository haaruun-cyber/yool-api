using System.Security.Claims;
using System.Security.Cryptography;
using aspbackend.Data;
using aspbackend.Models;
using aspbackend.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;

namespace aspbackend.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(MongoDbContext db, JwtService jwt, EmailService email, AnalyticsService analytics, IConfiguration config) : BaseApiController(db)
{
    public record RegisterDto(string Name, string Email, string Password);
    public record LoginDto(string Email, string Password);
    public record RefreshDto(string RefreshToken);
    public record ResetDto(string Password);

    [HttpGet("google/status")]
    public IActionResult GoogleStatus()
    {
        var appOrigin = (config["API_PUBLIC_URL"] ?? config["CLIENT_URL"] ?? "http://localhost:3000").TrimEnd('/');
        var enabled = !string.IsNullOrWhiteSpace(config["GOOGLE_CLIENT_ID"]) && !string.IsNullOrWhiteSpace(config["GOOGLE_CLIENT_SECRET"]);
        var callbackUrl = config["GOOGLE_CALLBACK_URL"] ?? $"{appOrigin}/api/auth/google/callback";
        return Ok(new
        {
            enabled,
            callbackUrl,
            clientUrl = appOrigin,
            startUrl = enabled ? $"{appOrigin}/api/auth/google" : null
        });
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Email) || dto.Password.Length < 8) return BadRequest(new { message = "Invalid registration" });
        var normalized = dto.Email.Trim().ToLowerInvariant();
        var users = await Db.AllAsync<User>(Tables.Users);
        if (users.Any(x => x.Email == normalized)) return Conflict(new { message = "Email already registered" });
        var now = DateTime.UtcNow;
        var user = new User { Id = ObjectId.GenerateNewId(), Name = dto.Name.Trim(), Email = normalized, Password = BCrypt.Net.BCrypt.HashPassword(dto.Password, 12), CreatedAt = now, UpdatedAt = now };
        await Db.InsertAsync(Tables.Users, user.Id, user);
        var (accessToken, refreshToken) = await IssueTokensAsync(user);
        return StatusCode(201, new
        {
            message = "Account created. Verify your email in Settings to create documents.",
            accessToken,
            refreshToken,
            user = UserPayload(user)
        });
    }

    [Authorize]
    [HttpPost("send-verification")]
    public async Task<IActionResult> SendVerification()
    {
        var user = await RequireUser();
        if (user is null) return Unauthorized(new { message = "Not authorized" });
        if (user.EmailVerified) return Ok(new { message = "Your email is already verified.", sent = false, emailVerified = true });

        var sent = await QueueVerificationEmailAsync(user);
        return Ok(new
        {
            message = sent
                ? "Verification email sent. Check your inbox."
                : "Could not send email. Check server email settings or try again later.",
            sent,
            emailVerified = false
        });
    }

    [HttpPost("resend-verification")]
    public async Task<IActionResult> ResendVerification(LoginDto dto)
    {
        var normalized = dto.Email.Trim().ToLowerInvariant();
        var user = (await Db.AllAsync<User>(Tables.Users)).FirstOrDefault(x => x.Email == normalized);
        if (user is not null && !user.EmailVerified) await QueueVerificationEmailAsync(user);
        return Ok(new { message = "If the account exists and is unverified, a verification email was sent." });
    }

    [HttpGet("verify-email/{token}")]
    public async Task<IActionResult> VerifyEmail(string token)
    {
        var user = (await Db.AllAsync<User>(Tables.Users)).FirstOrDefault(x => x.EmailVerificationToken == token && x.EmailVerificationExpires > DateTime.UtcNow);
        if (user is null) return BadRequest(new { message = "Invalid or expired token" });
        user.EmailVerified = true;
        user.EmailVerificationToken = null;
        user.EmailVerificationExpires = null;
        user.UpdatedAt = DateTime.UtcNow;
        await Db.ReplaceAsync(Tables.Users, user.Id, user);
        return Ok(new { message = "Email verified" });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginDto dto)
    {
        var normalized = dto.Email.Trim().ToLowerInvariant();
        var user = (await Db.AllAsync<User>(Tables.Users)).FirstOrDefault(x => x.Email == normalized);
        if (user is null || user.IsBlocked || string.IsNullOrWhiteSpace(user.Password)) return Unauthorized(new { message = "Invalid credentials" });
        if (!BCrypt.Net.BCrypt.Verify(dto.Password, user.Password)) return Unauthorized(new { message = "Invalid credentials" });
        var (accessToken, refreshToken) = await IssueTokensAsync(user);
        return Ok(new { accessToken, refreshToken, user = UserPayload(user) });
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(RefreshDto dto)
    {
        try
        {
            var principal = jwt.ValidateRefreshToken(dto.RefreshToken);
            var sub = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!ObjectId.TryParse(sub, out var id)) return Unauthorized(new { message = "Invalid refresh" });
            var user = await Db.GetAsync<User>(Tables.Users, id);
            if (user is null || user.IsBlocked || !user.RefreshTokens.Any(x => x.Hash == JwtService.Sha256(dto.RefreshToken))) return Unauthorized(new { message = "Invalid refresh" });
            return Ok(new { accessToken = jwt.SignAccessToken(user.Id) });
        }
        catch { return Unauthorized(new { message = "Invalid refresh" }); }
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(RefreshDto? dto)
    {
        var user = await RequireUser();
        if (user is null) return Unauthorized(new { message = "Not authorized" });
        if (!string.IsNullOrWhiteSpace(dto?.RefreshToken)) user.RefreshTokens.RemoveAll(x => x.Hash == JwtService.Sha256(dto.RefreshToken));
        else user.RefreshTokens = [];
        user.UpdatedAt = DateTime.UtcNow;
        await Db.ReplaceAsync(Tables.Users, user.Id, user);
        return Ok(new { message = "Logged out" });
    }

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(LoginDto dto)
    {
        var normalized = dto.Email.Trim().ToLowerInvariant();
        var user = (await Db.AllAsync<User>(Tables.Users)).FirstOrDefault(x => x.Email == normalized);
        var sent = false;
        if (user is not null)
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            user.PasswordResetToken = JwtService.Sha256(token);
            user.PasswordResetExpires = DateTime.UtcNow.AddHours(1);
            user.UpdatedAt = DateTime.UtcNow;
            await Db.ReplaceAsync(Tables.Users, user.Id, user);
            var url = $"{ClientBase()}/reset-password?token={token}";
            sent = await email.SendAsync(
                user.Email,
                "Reset your Yool password",
                $"<p>Hi {user.Name},</p><p><a href=\"{url}\">Reset your password</a> (expires in 1 hour).</p>",
                $"Reset your password: {url}");
        }

        return Ok(new
        {
            message = sent
                ? "Password reset email sent. Check your inbox."
                : "If the account exists, a reset email was sent when mail is configured."
        });
    }

    [HttpPost("reset-password/{token}")]
    public async Task<IActionResult> ResetPassword(string token, ResetDto dto)
    {
        if (dto.Password.Length < 8) return BadRequest(new { message = "Password must be at least 8 characters" });
        var hashed = JwtService.Sha256(token);
        var user = (await Db.AllAsync<User>(Tables.Users)).FirstOrDefault(x => x.PasswordResetToken == hashed && x.PasswordResetExpires > DateTime.UtcNow);
        if (user is null) return BadRequest(new { message = "Invalid or expired token" });
        user.Password = BCrypt.Net.BCrypt.HashPassword(dto.Password, 12);
        user.PasswordResetToken = null;
        user.PasswordResetExpires = null;
        user.RefreshTokens = [];
        user.UpdatedAt = DateTime.UtcNow;
        await Db.ReplaceAsync(Tables.Users, user.Id, user);
        return Ok(new { message = "Password updated" });
    }

    private bool GoogleEnabled() =>
        !string.IsNullOrWhiteSpace(config["GOOGLE_CLIENT_ID"])
        && !string.IsNullOrWhiteSpace(config["GOOGLE_CLIENT_SECRET"]);

    private string ClientBase() => (config["CLIENT_URL"] ?? "http://localhost:3000").TrimEnd('/');

    [HttpGet("google")]
    public IActionResult GoogleLogin()
    {
        if (!GoogleEnabled())
            return StatusCode(501, new { message = "Google OAuth is not configured. Set GOOGLE_CLIENT_ID, GOOGLE_CLIENT_SECRET, and GOOGLE_CALLBACK_URL." });

        var props = new AuthenticationProperties { RedirectUri = "/api/auth/google/complete" };
        return Challenge(props, GoogleDefaults.AuthenticationScheme);
    }

    [HttpGet("google/complete")]
    public async Task<IActionResult> GoogleComplete()
    {
        if (!GoogleEnabled())
            return Redirect($"{ClientBase()}/login?error=google_not_configured");

        var result = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!result.Succeeded)
            return Redirect($"{ClientBase()}/login?error=google_denied");

        var googleId = ClaimValue(result.Principal, ClaimTypes.NameIdentifier, "sub");
        var email = ClaimValue(result.Principal, ClaimTypes.Email, "email", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress");
        var name = ClaimValue(result.Principal, ClaimTypes.Name, "name", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name")
            ?? result.Principal.Identity?.Name
            ?? "Google User";
        var avatar = ClaimValue(result.Principal, "picture", "urn:google:picture") ?? "";

        if (string.IsNullOrWhiteSpace(googleId) || string.IsNullOrWhiteSpace(email))
            return Redirect($"{ClientBase()}/login?error=google_failed");

        var normalized = email.Trim().ToLowerInvariant();
        var users = await Db.AllAsync<User>(Tables.Users);
        var user = users.FirstOrDefault(u => u.GoogleId == googleId)
            ?? users.FirstOrDefault(u => u.Email == normalized);

        var now = DateTime.UtcNow;
        if (user is null)
        {
            user = new User
            {
                Id = ObjectId.GenerateNewId(),
                Name = name,
                Email = normalized,
                GoogleId = googleId,
                Avatar = avatar,
                EmailVerified = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            await Db.InsertAsync(Tables.Users, user.Id, user);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(user.GoogleId)) user.GoogleId = googleId;
            user.EmailVerified = true;
            if (string.IsNullOrWhiteSpace(user.Avatar) && !string.IsNullOrWhiteSpace(avatar)) user.Avatar = avatar;
            user.UpdatedAt = now;
            await Db.ReplaceAsync(Tables.Users, user.Id, user);
        }

        if (user.IsBlocked)
            return Redirect($"{ClientBase()}/login?error=account_blocked");

        var accessToken = jwt.SignAccessToken(user.Id);
        var refreshToken = jwt.SignRefreshToken(user.Id);
        user.RefreshTokens.Add(new RefreshTokenRecord { Hash = JwtService.Sha256(refreshToken), CreatedAt = now });
        if (user.RefreshTokens.Count > 10) user.RefreshTokens = user.RefreshTokens.TakeLast(10).ToList();
        user.LastLoginAt = now;
        user.LoginCount += 1;
        user.UpdatedAt = now;
        await Db.ReplaceAsync(Tables.Users, user.Id, user);
        await analytics.RecordLoginAsync();
        await analytics.RecordActiveUserAsync();

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect($"{ClientBase()}/auth/callback?accessToken={Uri.EscapeDataString(accessToken)}&refreshToken={Uri.EscapeDataString(refreshToken)}");
    }

    private async Task<(string AccessToken, string RefreshToken)> IssueTokensAsync(User user)
    {
        var accessToken = jwt.SignAccessToken(user.Id);
        var refreshToken = jwt.SignRefreshToken(user.Id);
        user.RefreshTokens.Add(new RefreshTokenRecord { Hash = JwtService.Sha256(refreshToken), CreatedAt = DateTime.UtcNow });
        if (user.RefreshTokens.Count > 10) user.RefreshTokens = user.RefreshTokens.TakeLast(10).ToList();
        user.LastLoginAt = DateTime.UtcNow;
        user.LoginCount += 1;
        user.UpdatedAt = DateTime.UtcNow;
        await Db.ReplaceAsync(Tables.Users, user.Id, user);
        await analytics.RecordLoginAsync();
        await analytics.RecordActiveUserAsync();
        return (accessToken, refreshToken);
    }

    private static object UserPayload(User user) => new
    {
        id = user.Id.ToString(),
        name = user.Name,
        email = user.Email,
        role = user.Role,
        subscriptionPlan = user.SubscriptionPlan,
        avatar = user.Avatar,
        emailVerified = user.EmailVerified
    };

    private static string? ClaimValue(ClaimsPrincipal principal, params string[] types)
    {
        foreach (var type in types)
        {
            var value = principal.FindFirstValue(type);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    private async Task<bool> QueueVerificationEmailAsync(User user)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        user.EmailVerificationToken = token;
        user.EmailVerificationExpires = DateTime.UtcNow.AddDays(1);
        user.UpdatedAt = DateTime.UtcNow;
        await Db.ReplaceAsync(Tables.Users, user.Id, user);
        var verifyUrl = $"{ClientBase()}/verify-email?token={token}";
        return await email.SendAsync(
            user.Email,
            "Verify your Yool email",
            $"<p>Hi {user.Name},</p><p>Click the link below to verify your email and unlock document creation:</p><p><a href=\"{verifyUrl}\">Verify email</a></p><p>This link expires in 24 hours.</p>",
            $"Verify your email: {verifyUrl}");
    }
}
