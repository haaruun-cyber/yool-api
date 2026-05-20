using aspbackend.Data;
using aspbackend.Models;
using aspbackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;

namespace aspbackend.Controllers;

[ApiController]
[Authorize]
[Route("api/admin")]
public sealed class AdminController(MongoDbContext db, AnalyticsService analytics) : BaseApiController(db)
{
    public record BlockDto(bool IsBlocked);

    [HttpGet("users")]
    public async Task<IActionResult> Users()
    {
        if (!await IsAdmin()) return StatusCode(403, new { message = "Admin only" });
        return Ok((await Db.AllAsync<User>(Tables.Users)).OrderByDescending(x => x.CreatedAt).Take(200));
    }

    [HttpPatch("users/{id}/block")]
    public async Task<IActionResult> Block(string id, BlockDto dto)
    {
        if (!await IsAdmin()) return StatusCode(403, new { message = "Admin only" });
        if (!ObjectId.TryParse(id, out var oid)) return NotFound(new { message = "Not found" });
        var user = await Db.GetAsync<User>(Tables.Users, oid);
        if (user is null) return NotFound(new { message = "Not found" });
        user.IsBlocked = dto.IsBlocked;
        user.UpdatedAt = DateTime.UtcNow;
        await Db.ReplaceAsync(Tables.Users, user.Id, user);
        return Ok(user);
    }

    [HttpGet("subscriptions")]
    public async Task<IActionResult> Subscriptions()
    {
        if (!await IsAdmin()) return StatusCode(403, new { message = "Admin only" });
        return Ok((await Db.AllAsync<Subscription>(Tables.Subscriptions)).OrderByDescending(x => x.UpdatedAt).Take(200));
    }

    [HttpGet("analytics")]
    public async Task<IActionResult> Analytics()
    {
        if (!await IsAdmin()) return StatusCode(403, new { message = "Admin only" });
        await analytics.SyncPaidUsersCountAsync();
        var users = await Db.AllAsync<User>(Tables.Users);
        var docs = await Db.AllAsync<Document>(Tables.Documents);
        var daily = (await Db.AllAsync<AnalyticsDaily>(Tables.AnalyticsDaily)).OrderByDescending(x => x.Date).Take(30).ToList();
        var paidUsers = users.LongCount(x => x.SubscriptionPlan is "pro" or "team");
        return Ok(new
        {
            totalUsers = users.LongCount(),
            paidUsers,
            blockedUsers = users.LongCount(x => x.IsBlocked),
            documents = docs.LongCount(),
            activeUsersLast7Days = users.LongCount(x => x.LastLoginAt >= DateTime.UtcNow.AddDays(-7)),
            churnRateApprox = Math.Round(daily.Sum(x => x.Churned) / Math.Max(1d, paidUsers), 4),
            daily
        });
    }

    private async Task<bool> IsAdmin() => (await RequireUser())?.Role == "admin";
}
