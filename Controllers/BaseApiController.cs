using System.Security.Claims;
using System.Text.Json;
using aspbackend.Data;
using aspbackend.Models;
using aspbackend.Services;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;

namespace aspbackend.Controllers;

public abstract class BaseApiController(MongoDbContext db) : ControllerBase
{
    protected MongoDbContext Db { get; } = db;

    protected ObjectId CurrentUserId
    {
        get
        {
            var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
            return ObjectId.TryParse(sub, out var id) ? id : ObjectId.Empty;
        }
    }

    protected async Task<User?> CurrentUserAsync() => (await Db.AllAsync<User>(Tables.Users)).FirstOrDefault(u => u.Id == CurrentUserId && !u.IsBlocked);

    protected async Task<User?> RequireUser()
    {
        var user = await CurrentUserAsync();
        return user;
    }

    protected IActionResult? RequireVerifiedEmail(User user) =>
        user.EmailVerified
            ? null
            : StatusCode(403, new
            {
                message = "Verify your email in Settings before creating documents.",
                code = "email_not_verified"
            });

    protected static bool CanAccess(Document doc, ObjectId userId, string permission = "read")
    {
        if (doc.OwnerId == userId) return true;
        var share = doc.SharedUsers.FirstOrDefault(x => x.User == userId);
        return share is not null && (permission == "read" || share.Permission == "edit");
    }

    protected async Task<Document?> LoadDocument(string id, ObjectId userId, string permission = "read")
    {
        if (!ObjectId.TryParse(id, out var objectId)) return null;
        var doc = await Db.GetAsync<Document>(Tables.Documents, objectId);
        return doc is not null && CanAccess(doc, userId, permission) ? doc : null;
    }

    protected static BsonValue ToBson(JsonElement? element)
    {
        if (element is null) return "";
        return BsonDocument.Parse("{\"v\":" + element.Value.GetRawText() + "}")["v"];
    }

    protected async Task Snapshot(Document doc, ObjectId userId)
    {
        var version = new DocumentVersion
        {
            Id = ObjectId.GenerateNewId(),
            DocumentId = doc.Id,
            Title = doc.Title,
            Content = doc.Content,
            CreatedBy = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await Db.InsertAsync(Tables.DocumentVersions, version.Id, version);
    }
}
