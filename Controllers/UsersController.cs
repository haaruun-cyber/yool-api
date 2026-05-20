using aspbackend.Data;
using aspbackend.Models;
using aspbackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace aspbackend.Controllers;

[ApiController]
[Authorize]
[Route("api/users")]
public sealed class UsersController(MongoDbContext db, CloudinaryService cloudinary) : BaseApiController(db)
{
    public record ProfileDto(string? Name, string? Avatar);

    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var user = await RequireUser();
        return user is null ? Unauthorized(new { message = "Not authorized" }) : Ok(new { id = user.Id.ToString(), user.Name, user.Email, user.Avatar, user.Role, user.SubscriptionPlan, user.EmailVerified, user.CreatedAt });
    }

    [HttpPatch("me")]
    public async Task<IActionResult> Update(ProfileDto dto)
    {
        var user = await RequireUser();
        if (user is null) return Unauthorized(new { message = "Not authorized" });
        if (!string.IsNullOrWhiteSpace(dto.Name)) user.Name = dto.Name.Trim();
        if (dto.Avatar is not null) user.Avatar = dto.Avatar;
        user.UpdatedAt = DateTime.UtcNow;
        await Db.ReplaceAsync(Tables.Users, user.Id, user);
        return Ok(new { id = user.Id.ToString(), user.Name, user.Email, user.Avatar, user.Role, user.SubscriptionPlan });
    }

    [HttpPost("me/avatar")]
    public async Task<IActionResult> UploadAvatar(IFormFile? file)
    {
        var user = await RequireUser();
        if (user is null) return Unauthorized(new { message = "Not authorized" });
        if (file is null) return BadRequest(new { message = "File required" });
        user.Avatar = await cloudinary.UploadAsync(file, "avatars");
        user.UpdatedAt = DateTime.UtcNow;
        await Db.ReplaceAsync(Tables.Users, user.Id, user);
        return Ok(new { avatar = user.Avatar });
    }

    [HttpGet("me/usage")]
    public async Task<IActionResult> Usage()
    {
        var user = await RequireUser();
        if (user is null) return Unauthorized(new { message = "Not authorized" });
        var used = (await Db.AllAsync<Document>(Tables.Documents)).LongCount(x => x.OwnerId == user.Id && !x.IsArchived);
        int? limit = user.SubscriptionPlan == "free" ? 5 : null;
        long? remaining = limit is null ? null : Math.Max(0, limit.Value - used);
        return Ok(new { plan = user.SubscriptionPlan, documents = new { used, limit, remaining }, ai = new { enabled = user.SubscriptionPlan is "pro" or "team" }, premiumTemplates = user.SubscriptionPlan is "pro" or "team" });
    }
}
