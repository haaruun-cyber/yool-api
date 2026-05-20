using aspbackend.Data;
using aspbackend.Models;
using aspbackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;

namespace aspbackend.Controllers;

[ApiController]
[Authorize]
[Route("api/collaboration")]
public sealed class CollaborationController(MongoDbContext db, NotificationService notifications) : BaseApiController(db)
{
    public record InviteDto(string UserId, string DocumentId, string? Message);
    public record RoomInviteDto(string Email, string? Permission);

    [HttpGet("rooms/{documentId}")]
    public async Task<IActionResult> Room(string documentId)
    {
        var doc = await LoadDocument(documentId, CurrentUserId);
        if (doc is null) return StatusCode(403, new { message = "No collaboration access" });
        var role = doc.OwnerId == CurrentUserId ? "owner" : "collaborator";
        return Ok(new { documentId = doc.Id.ToString(), title = doc.Title, role });
    }

    [HttpPost("invites")]
    public async Task<IActionResult> SendInvite(InviteDto dto)
    {
        if (!ObjectId.TryParse(dto.UserId, out var userId) || !ObjectId.TryParse(dto.DocumentId, out var documentId))
            return BadRequest(new { message = "Invalid userId or documentId" });

        var doc = await Db.GetAsync<Document>(Tables.Documents, documentId);
        if (doc is null || doc.OwnerId != CurrentUserId)
            return StatusCode(403, new { message = "Not allowed" });

        await notifications.NotifyAsync(
            userId,
            "collaboration_invite",
            dto.Message ?? $"Collaboration invite for {doc.Title}",
            new BsonDocument { ["documentId"] = doc.Id.ToString() });

        return Ok(new { sent = true });
    }

    [HttpPost("rooms/{documentId}/invite")]
    public async Task<IActionResult> InviteByEmail(string documentId, RoomInviteDto dto)
    {
        var doc = await LoadDocument(documentId, CurrentUserId, "edit");
        if (doc is null) return StatusCode(403, new { message = "Cannot access document" });

        var email = dto.Email.Trim().ToLowerInvariant();
        var users = await Db.AllAsync<User>(Tables.Users);
        var target = users.FirstOrDefault(u => u.Email == email);
        if (target is not null)
        {
            if (!doc.SharedUsers.Any(x => x.User == target.Id))
            {
                doc.SharedUsers.Add(new DocumentShare { User = target.Id, Permission = dto.Permission ?? "read" });
                doc.UpdatedAt = DateTime.UtcNow;
                await Db.ReplaceAsync(Tables.Documents, doc.Id, doc);
            }

            await notifications.NotifyAsync(
                target.Id,
                "collaboration_invite",
                $"You were invited to collaborate on {doc.Title}",
                new BsonDocument { ["documentId"] = doc.Id.ToString() });
        }
        else
        {
            doc.PendingInvites.RemoveAll(x => x.Email == email);
            doc.PendingInvites.Add(new PendingInvite { Email = email, Permission = dto.Permission ?? "read" });
            doc.UpdatedAt = DateTime.UtcNow;
            await Db.ReplaceAsync(Tables.Documents, doc.Id, doc);
        }

        return Ok(new { invited = true, email, permission = dto.Permission ?? "read" });
    }
}
