using aspbackend.Data;
using aspbackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace aspbackend.Controllers;

[ApiController]
[Authorize]
[Route("api/ai")]
public sealed class AiController(MongoDbContext db, OpenAiService openAi) : BaseApiController(db)
{
    public record TextDto(string Text);
    public record WriteDto(string Instruction, string Draft);

    [HttpPost("summarize")]
    public async Task<IActionResult> Summarize(TextDto dto)
    {
        var gate = await RequireAiPlan();
        return gate ?? Ok(new { summary = await openAi.SummarizeNote(CurrentUserId, dto.Text) });
    }

    [HttpPost("meeting-summary")]
    public async Task<IActionResult> Meeting(TextDto dto)
    {
        var gate = await RequireAiPlan();
        return gate ?? Ok(new { summary = await openAi.SummarizeMeeting(CurrentUserId, dto.Text) });
    }

    [HttpPost("generate-tasks")]
    public async Task<IActionResult> Generate(TextDto dto)
    {
        var gate = await RequireAiPlan();
        return gate ?? Ok(new { tasks = await openAi.GenerateTasks(CurrentUserId, dto.Text) });
    }

    [HttpPost("write")]
    public async Task<IActionResult> Write(WriteDto dto)
    {
        var gate = await RequireAiPlan();
        return gate ?? Ok(new { output = await openAi.WriteAssist(CurrentUserId, dto.Instruction, dto.Draft) });
    }

    private async Task<IActionResult?> RequireAiPlan()
    {
        var user = await RequireUser();
        if (user is null) return Unauthorized(new { message = "Not authorized" });
        return user.SubscriptionPlan is "pro" or "team" ? null : StatusCode(402, new { message = "AI tools require Pro or Team" });
    }
}
