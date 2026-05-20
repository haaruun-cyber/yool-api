using aspbackend.Data;
using aspbackend.Models;
using aspbackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;

namespace aspbackend.Controllers;

[ApiController]
[Authorize]
[Route("api/notifications")]
public sealed class NotificationsController(MongoDbContext db) : BaseApiController(db)
{
    [HttpGet]
    public async Task<IActionResult> List() => Ok((await Db.AllAsync<Notification>(Tables.Notifications)).Where(x => x.UserId == CurrentUserId).OrderByDescending(x => x.CreatedAt).Take(100));

    [HttpPatch("{id}/read")]
    public async Task<IActionResult> Read(string id)
    {
        if (!ObjectId.TryParse(id, out var oid)) return NotFound(new { message = "Not found" });
        var n = await Db.GetAsync<Notification>(Tables.Notifications, oid);
        if (n is null || n.UserId != CurrentUserId) return NotFound(new { message = "Not found" });
        n.IsRead = true;
        n.UpdatedAt = DateTime.UtcNow;
        await Db.ReplaceAsync(Tables.Notifications, n.Id, n);
        return Ok(n);
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> ReadAll()
    {
        var items = (await Db.AllAsync<Notification>(Tables.Notifications)).Where(x => x.UserId == CurrentUserId && !x.IsRead);
        foreach (var item in items)
        {
            item.IsRead = true;
            item.UpdatedAt = DateTime.UtcNow;
            await Db.ReplaceAsync(Tables.Notifications, item.Id, item);
        }
        return Ok(new { updated = true });
    }
}
