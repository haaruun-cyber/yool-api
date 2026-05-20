using System.Text.Json;
using aspbackend.Data;
using aspbackend.Models;
using aspbackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;

namespace aspbackend.Controllers;

[ApiController]
[Authorize]
[Route("api/templates")]
public sealed class TemplatesController(MongoDbContext db, AnalyticsService analytics) : BaseApiController(db)
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? category)
    {
        var user = await RequireUser();
        if (user is null) return Unauthorized(new { message = "Not authorized" });
        var items = (await Db.AllAsync<Template>(Tables.Templates))
            .Where(x => x.IsActive)
            .Where(x => string.IsNullOrWhiteSpace(category) || x.Category == category)
            .Where(x => user.SubscriptionPlan != "free" || !x.IsPremium)
            .OrderBy(x => x.Title);
        return Ok(items);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id)
    {
        var user = await RequireUser();
        if (user is null || !ObjectId.TryParse(id, out var oid)) return NotFound(new { message = "Template not found" });
        var tpl = await Db.GetAsync<Template>(Tables.Templates, oid);
        if (tpl is null || !tpl.IsActive) return NotFound(new { message = "Template not found" });
        if (tpl.IsPremium && user.SubscriptionPlan == "free") return StatusCode(402, new { message = "Premium template" });
        return Ok(tpl);
    }

    [HttpPost("{id}/use")]
    public async Task<IActionResult> Use(string id)
    {
        var user = await RequireUser();
        if (user is null || !ObjectId.TryParse(id, out var oid)) return NotFound(new { message = "Template not found" });
        var tpl = await Db.GetAsync<Template>(Tables.Templates, oid);
        if (tpl is null || !tpl.IsActive) return NotFound(new { message = "Template not found" });
        if (tpl.IsPremium && user.SubscriptionPlan == "free") return StatusCode(402, new { message = "Premium template" });
        var verifyBlock = RequireVerifiedEmail(user);
        if (verifyBlock is not null) return verifyBlock;
        if (user.SubscriptionPlan == "free" && (await Db.AllAsync<Document>(Tables.Documents)).Count(x => x.OwnerId == user.Id && !x.IsArchived) >= 5) return StatusCode(402, new { message = "Document limit reached" });
        var now = DateTime.UtcNow;
        var doc = new Document { Id = ObjectId.GenerateNewId(), Title = tpl.Title, Content = tpl.Content, Type = tpl.Category, OwnerId = user.Id, Tags = ["from-template"], CreatedAt = now, UpdatedAt = now, LastEdited = now };
        await Db.InsertAsync(Tables.Documents, doc.Id, doc);
        await Snapshot(doc, user.Id);
        await analytics.RecordDocumentCreatedAsync();
        return StatusCode(201, doc);
    }
}

[ApiController]
[Authorize]
[Route("api/admin/templates")]
public sealed class AdminTemplatesController(MongoDbContext db) : BaseApiController(db)
{
    public sealed record TemplateDto(string Title, string Category, JsonElement Content, bool? IsPremium, bool? IsActive);

    [HttpPost]
    public async Task<IActionResult> Create(TemplateDto dto)
    {
        if (!await IsAdmin()) return StatusCode(403, new { message = "Admin only" });
        var now = DateTime.UtcNow;
        var tpl = new Template { Id = ObjectId.GenerateNewId(), Title = dto.Title, Category = dto.Category, Content = ToBson(dto.Content), IsPremium = dto.IsPremium ?? false, IsActive = dto.IsActive ?? true, CreatedAt = now, UpdatedAt = now };
        await Db.InsertAsync(Tables.Templates, tpl.Id, tpl);
        return StatusCode(201, tpl);
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(string id, TemplateDto dto)
    {
        if (!await IsAdmin()) return StatusCode(403, new { message = "Admin only" });
        if (!ObjectId.TryParse(id, out var oid)) return NotFound(new { message = "Not found" });
        var tpl = await Db.GetAsync<Template>(Tables.Templates, oid);
        if (tpl is null) return NotFound(new { message = "Not found" });
        if (!string.IsNullOrWhiteSpace(dto.Title)) tpl.Title = dto.Title;
        if (!string.IsNullOrWhiteSpace(dto.Category)) tpl.Category = dto.Category;
        tpl.Content = ToBson(dto.Content);
        if (dto.IsPremium is not null) tpl.IsPremium = dto.IsPremium.Value;
        if (dto.IsActive is not null) tpl.IsActive = dto.IsActive.Value;
        tpl.UpdatedAt = DateTime.UtcNow;
        await Db.ReplaceAsync(Tables.Templates, tpl.Id, tpl);
        return Ok(tpl);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        if (!await IsAdmin()) return StatusCode(403, new { message = "Admin only" });
        if (ObjectId.TryParse(id, out var oid)) await Db.DeleteAsync(Tables.Templates, oid);
        return Ok(new { message = "Deleted" });
    }

    private async Task<bool> IsAdmin() => (await RequireUser())?.Role == "admin";
}
