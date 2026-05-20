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
[Route("api/documents")]
public sealed class DocumentsController(MongoDbContext db, CloudinaryService cloudinary, NotificationService notifications, AnalyticsService analytics) : BaseApiController(db)
{
    public sealed record DocumentDto(string? Title, JsonElement? Content, string? Type, bool? IsPrivate, bool? IsFavorite, bool? IsArchived, bool? IsPinned, string? CoverImage, string? Status, DateTime? Deadline, List<string>? Tags);
    public sealed record BoolDto(bool IsPinned, bool IsFavorite);
    public sealed record ShareDto(string Email, string? Permission);
    public sealed record PermissionDto(string Permission);

    [HttpPost]
    public async Task<IActionResult> Create(DocumentDto dto)
    {
        var user = await RequireUser();
        if (user is null) return Unauthorized(new { message = "Not authorized" });
        var verifyBlock = RequireVerifiedEmail(user);
        if (verifyBlock is not null) return verifyBlock;
        var docs = await Db.AllAsync<Document>(Tables.Documents);
        if (user.SubscriptionPlan == "free" && docs.Count(x => x.OwnerId == user.Id && !x.IsArchived) >= 5) return StatusCode(402, new { message = "Free plan allows up to 5 active documents" });
        if (string.IsNullOrWhiteSpace(dto.Title)) return BadRequest(new { message = "title is required" });
        var now = DateTime.UtcNow;
        var doc = new Document { Id = ObjectId.GenerateNewId(), Title = dto.Title.Trim(), Content = ToBson(dto.Content), Type = dto.Type is not null && Document.DocumentTypes.Contains(dto.Type) ? dto.Type : "note", OwnerId = user.Id, IsPrivate = dto.IsPrivate ?? true, Tags = dto.Tags ?? [], CoverImage = dto.CoverImage ?? "", Status = dto.Status ?? "not_started", Deadline = dto.Deadline, CreatedAt = now, UpdatedAt = now, LastEdited = now };
        await Db.InsertAsync(Tables.Documents, doc.Id, doc);
        await Snapshot(doc, user.Id);
        await analytics.RecordDocumentCreatedAsync();
        return StatusCode(201, doc);
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? archived, [FromQuery] string? favorite, [FromQuery] string? type)
    {
        var user = await RequireUser();
        if (user is null) return Unauthorized(new { message = "Not authorized" });
        var docs = (await Db.AllAsync<Document>(Tables.Documents)).Where(x => CanAccess(x, user.Id));
        if (archived == "true") docs = docs.Where(x => x.IsArchived);
        else if (archived != "all") docs = docs.Where(x => !x.IsArchived);
        if (favorite == "true") docs = docs.Where(x => x.IsFavorite);
        if (!string.IsNullOrWhiteSpace(type)) docs = docs.Where(x => x.Type == type);
        return Ok(docs.OrderByDescending(x => x.IsPinned).ThenByDescending(x => x.UpdatedAt));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id)
    {
        var doc = await LoadDocument(id, CurrentUserId);
        return doc is null ? NotFound(new { message = "Document not found" }) : Ok(doc);
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(string id, DocumentDto dto)
    {
        var doc = await LoadDocument(id, CurrentUserId, "edit");
        if (doc is null) return StatusCode(403, new { message = "Not authorized for this document" });
        await Snapshot(doc, CurrentUserId);
        if (!string.IsNullOrWhiteSpace(dto.Title)) doc.Title = dto.Title.Trim();
        if (dto.Content is not null) doc.Content = ToBson(dto.Content);
        if (!string.IsNullOrWhiteSpace(dto.Type)) doc.Type = dto.Type;
        if (dto.IsPrivate is not null) doc.IsPrivate = dto.IsPrivate.Value;
        if (dto.Tags is not null) doc.Tags = dto.Tags;
        if (dto.CoverImage is not null) doc.CoverImage = dto.CoverImage;
        if (dto.Status is not null) doc.Status = dto.Status;
        if (dto.Deadline is not null) doc.Deadline = dto.Deadline;
        doc.LastEdited = DateTime.UtcNow;
        doc.UpdatedAt = DateTime.UtcNow;
        await Db.ReplaceAsync(Tables.Documents, doc.Id, doc);
        return Ok(doc);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        if (!ObjectId.TryParse(id, out var objectId)) return NotFound(new { message = "Document not found" });
        var doc = await Db.GetAsync<Document>(Tables.Documents, objectId);
        if (doc is null || doc.OwnerId != CurrentUserId) return NotFound(new { message = "Document not found" });
        await Db.DeleteWhereAsync<DocumentVersion>(Tables.DocumentVersions, x => x.DocumentId == doc.Id);
        await Db.DeleteAsync(Tables.Documents, doc.Id);
        return Ok(new { message = "Deleted" });
    }

    [HttpPost("{id}/duplicate")]
    public async Task<IActionResult> Duplicate(string id)
    {
        var user = await RequireUser();
        if (user is null) return Unauthorized(new { message = "Not authorized" });
        var source = await LoadDocument(id, user.Id);
        if (source is null) return NotFound(new { message = "Document not found" });
        if (user.SubscriptionPlan == "free" && (await Db.AllAsync<Document>(Tables.Documents)).Count(x => x.OwnerId == user.Id && !x.IsArchived) >= 5) return StatusCode(402, new { message = "Free plan allows up to 5 active documents" });
        var now = DateTime.UtcNow;
        var copy = new Document { Id = ObjectId.GenerateNewId(), Title = $"{source.Title} (copy)", Content = source.Content, Type = source.Type, OwnerId = user.Id, IsPrivate = true, Tags = source.Tags, CoverImage = source.CoverImage, CreatedAt = now, UpdatedAt = now, LastEdited = now };
        await Db.InsertAsync(Tables.Documents, copy.Id, copy);
        await Snapshot(copy, user.Id);
        await analytics.RecordDocumentCreatedAsync();
        return StatusCode(201, copy);
    }

    [HttpPost("{id}/archive")] public Task<IActionResult> Archive(string id) => ToggleArchived(id, true);
    [HttpPost("{id}/restore")] public Task<IActionResult> Restore(string id) => ToggleArchived(id, false);

    [HttpPatch("{id}/pin")]
    public async Task<IActionResult> Pin(string id, BoolDto dto)
    {
        var doc = await LoadDocument(id, CurrentUserId, "edit");
        if (doc is null) return StatusCode(403, new { message = "Not authorized for this document" });
        doc.IsPinned = dto.IsPinned; doc.UpdatedAt = DateTime.UtcNow;
        await Db.ReplaceAsync(Tables.Documents, doc.Id, doc);
        return Ok(doc);
    }

    [HttpPatch("{id}/favorite")]
    public async Task<IActionResult> Favorite(string id, BoolDto dto)
    {
        var doc = await LoadDocument(id, CurrentUserId, "edit");
        if (doc is null) return StatusCode(403, new { message = "Not authorized for this document" });
        doc.IsFavorite = dto.IsFavorite; doc.UpdatedAt = DateTime.UtcNow;
        await Db.ReplaceAsync(Tables.Documents, doc.Id, doc);
        return Ok(doc);
    }

    [HttpGet("{id}/versions")]
    public async Task<IActionResult> Versions(string id)
    {
        var doc = await LoadDocument(id, CurrentUserId);
        if (doc is null) return StatusCode(403, new { message = "Not authorized for this document" });
        return Ok((await Db.AllAsync<DocumentVersion>(Tables.DocumentVersions)).Where(x => x.DocumentId == doc.Id).OrderByDescending(x => x.CreatedAt).Take(50));
    }

    [HttpPost("{id}/versions/{versionId}/revert")]
    public async Task<IActionResult> Revert(string id, string versionId)
    {
        var doc = await LoadDocument(id, CurrentUserId, "edit");
        if (doc is null || !ObjectId.TryParse(versionId, out var vid)) return NotFound(new { message = "Version not found" });
        var version = await Db.GetAsync<DocumentVersion>(Tables.DocumentVersions, vid);
        if (version is null || version.DocumentId != doc.Id) return NotFound(new { message = "Version not found" });
        await Snapshot(doc, CurrentUserId);
        doc.Title = version.Title; doc.Content = version.Content; doc.UpdatedAt = DateTime.UtcNow; doc.LastEdited = DateTime.UtcNow;
        await Db.ReplaceAsync(Tables.Documents, doc.Id, doc);
        return Ok(doc);
    }

    [HttpPost("{id}/share")]
    public async Task<IActionResult> Share(string id, ShareDto dto)
    {
        var doc = await LoadDocument(id, CurrentUserId, "edit");
        var me = await RequireUser();
        if (doc is null || me is null) return StatusCode(403, new { message = "Not authorized for this document" });
        var normalized = dto.Email.Trim().ToLowerInvariant();
        var permission = dto.Permission is "edit" ? "edit" : "read";
        var target = (await Db.AllAsync<User>(Tables.Users)).FirstOrDefault(x => x.Email == normalized);
        if (target is not null)
        {
            if (target.Id == doc.OwnerId) return BadRequest(new { message = "Cannot share with owner" });
            var share = doc.SharedUsers.FirstOrDefault(x => x.User == target.Id);
            if (share is null) doc.SharedUsers.Add(new DocumentShare { User = target.Id, Permission = permission });
            else share.Permission = permission;
            doc.PendingInvites.RemoveAll(x => x.Email == normalized);
            await notifications.NotifyAsync(target.Id, "document_shared", $"{me.Name} shared \"{doc.Title}\" with you", new BsonDocument { ["documentId"] = doc.Id, ["permission"] = permission });
        }
        else
        {
            var pending = doc.PendingInvites.FirstOrDefault(x => x.Email == normalized);
            if (pending is null) doc.PendingInvites.Add(new PendingInvite { Email = normalized, Permission = permission });
            else pending.Permission = permission;
        }
        doc.UpdatedAt = DateTime.UtcNow;
        await Db.ReplaceAsync(Tables.Documents, doc.Id, doc);
        return Ok(doc);
    }

    [HttpPatch("{id}/share/{userId}")]
    public async Task<IActionResult> UpdateShare(string id, string userId, PermissionDto dto)
    {
        var doc = await LoadDocument(id, CurrentUserId, "edit");
        if (doc is null || !ObjectId.TryParse(userId, out var uid)) return NotFound(new { message = "Share not found" });
        var share = doc.SharedUsers.FirstOrDefault(x => x.User == uid);
        if (share is null) return NotFound(new { message = "Share not found" });
        share.Permission = dto.Permission == "edit" ? "edit" : "read";
        await Db.ReplaceAsync(Tables.Documents, doc.Id, doc);
        return Ok(doc);
    }

    [HttpDelete("{id}/share/{userId}")]
    public async Task<IActionResult> RevokeShare(string id, string userId)
    {
        var doc = await LoadDocument(id, CurrentUserId, "edit");
        if (doc is null || !ObjectId.TryParse(userId, out var uid)) return NotFound(new { message = "Document not found" });
        doc.SharedUsers.RemoveAll(x => x.User == uid);
        await Db.ReplaceAsync(Tables.Documents, doc.Id, doc);
        return Ok(doc);
    }

    [HttpDelete("{id}/pending/{email}")]
    public async Task<IActionResult> RevokePending(string id, string email)
    {
        var doc = await LoadDocument(id, CurrentUserId, "edit");
        if (doc is null) return NotFound(new { message = "Document not found" });
        doc.PendingInvites.RemoveAll(x => x.Email == email.ToLowerInvariant());
        await Db.ReplaceAsync(Tables.Documents, doc.Id, doc);
        return Ok(doc);
    }

    [HttpPost("{id}/cover")]
    public async Task<IActionResult> UploadCover(string id, IFormFile? file)
    {
        var doc = await LoadDocument(id, CurrentUserId, "edit");
        if (doc is null) return StatusCode(403, new { message = "Not authorized for this document" });
        if (file is null) return BadRequest(new { message = "File required" });
        doc.CoverImage = await cloudinary.UploadAsync(file, "covers");
        await Db.ReplaceAsync(Tables.Documents, doc.Id, doc);
        return Ok(new { coverImage = doc.CoverImage });
    }

    private async Task<IActionResult> ToggleArchived(string id, bool archived)
    {
        var doc = await LoadDocument(id, CurrentUserId, "edit");
        if (doc is null) return StatusCode(403, new { message = "Not authorized for this document" });
        doc.IsArchived = archived; doc.UpdatedAt = DateTime.UtcNow;
        await Db.ReplaceAsync(Tables.Documents, doc.Id, doc);
        return Ok(doc);
    }
}
