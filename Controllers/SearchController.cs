using aspbackend.Data;
using aspbackend.Models;
using aspbackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace aspbackend.Controllers;

[ApiController]
[Authorize]
[Route("api/search")]
public sealed class SearchController(MongoDbContext db) : BaseApiController(db)
{
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] int limit = 20, [FromQuery] int skip = 0)
    {
        if (string.IsNullOrWhiteSpace(q)) return BadRequest(new { message = "Query parameter q is required" });
        var needle = q.Trim();
        var results = (await Db.AllAsync<Document>(Tables.Documents))
            .Where(x => CanAccess(x, CurrentUserId) && (x.Title.Contains(needle, StringComparison.OrdinalIgnoreCase) || x.Tags.Any(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(x => x.UpdatedAt)
            .Skip(skip)
            .Take(Math.Clamp(limit, 1, 100));
        return Ok(results);
    }
}
