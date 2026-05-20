using aspbackend.Data;
using aspbackend.Models;
using aspbackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;

namespace aspbackend.Controllers;

[ApiController]
[Authorize]
[Route("api/tasks")]
public sealed class TasksController(MongoDbContext db, NotificationService notifications) : BaseApiController(db)
{
    public record TaskDto(string? Title, string? Description, string? Status, DateTime? DueDate, string? Priority, string? DocumentId);
    public record DeadlineDto(DateTime? DueDate);

    [HttpGet("document/{documentId}")]
    public async Task<IActionResult> List(string documentId)
    {
        var doc = await LoadDocument(documentId, CurrentUserId);
        if (doc is null) return StatusCode(403, new { message = "Cannot access document" });
        return Ok((await Db.AllAsync<TaskItem>(Tables.Tasks)).Where(x => x.DocumentId == doc.Id).OrderByDescending(x => x.CreatedAt));
    }

    [HttpPost]
    public async Task<IActionResult> Create(TaskDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Title) || !ObjectId.TryParse(dto.DocumentId, out var docId)) return BadRequest(new { message = "title and documentId are required" });
        var doc = await LoadDocument(docId.ToString(), CurrentUserId, "edit");
        if (doc is null) return StatusCode(403, new { message = "Cannot access document" });
        var now = DateTime.UtcNow;
        var task = new TaskItem { Id = ObjectId.GenerateNewId(), Title = dto.Title.Trim(), Description = dto.Description ?? "", Status = dto.Status ?? "open", DueDate = dto.DueDate, Priority = dto.Priority ?? "medium", DocumentId = doc.Id, UserId = CurrentUserId, CreatedAt = now, UpdatedAt = now };
        await Db.InsertAsync(Tables.Tasks, task.Id, task);
        return StatusCode(201, task);
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(string id, TaskDto dto)
    {
        if (!ObjectId.TryParse(id, out var tid)) return NotFound(new { message = "Task not found" });
        var task = await Db.GetAsync<TaskItem>(Tables.Tasks, tid);
        if (task is null || task.UserId != CurrentUserId) return NotFound(new { message = "Task not found" });
        if (dto.Title is not null) task.Title = dto.Title;
        if (dto.Description is not null) task.Description = dto.Description;
        if (dto.Status is not null) task.Status = dto.Status;
        if (dto.DueDate is not null) task.DueDate = dto.DueDate;
        if (dto.Priority is not null) task.Priority = dto.Priority;
        task.UpdatedAt = DateTime.UtcNow;
        await Db.ReplaceAsync(Tables.Tasks, task.Id, task);
        return Ok(task);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        if (!ObjectId.TryParse(id, out var tid)) return NotFound(new { message = "Task not found" });
        var task = await Db.GetAsync<TaskItem>(Tables.Tasks, tid);
        if (task is null || task.UserId != CurrentUserId) return NotFound(new { message = "Task not found" });
        await Db.DeleteAsync(Tables.Tasks, task.Id);
        return Ok(new { message = "Deleted" });
    }

    [HttpPost("{id}/complete")]
    public async Task<IActionResult> Complete(string id)
    {
        if (!ObjectId.TryParse(id, out var tid)) return NotFound(new { message = "Task not found" });
        var task = await Db.GetAsync<TaskItem>(Tables.Tasks, tid);
        if (task is null || task.UserId != CurrentUserId) return NotFound(new { message = "Task not found" });
        task.Status = "done"; task.CompletedAt = DateTime.UtcNow; task.UpdatedAt = DateTime.UtcNow;
        await Db.ReplaceAsync(Tables.Tasks, task.Id, task);
        return Ok(task);
    }

    [HttpPatch("{id}/deadline")]
    public async Task<IActionResult> Deadline(string id, DeadlineDto dto)
    {
        if (!ObjectId.TryParse(id, out var tid)) return NotFound(new { message = "Task not found" });
        var task = await Db.GetAsync<TaskItem>(Tables.Tasks, tid);
        if (task is null || task.UserId != CurrentUserId) return NotFound(new { message = "Task not found" });
        task.DueDate = dto.DueDate; task.UpdatedAt = DateTime.UtcNow;
        await Db.ReplaceAsync(Tables.Tasks, task.Id, task);
        if (task.DueDate is not null) await notifications.NotifyAsync(CurrentUserId, "task_deadline", $"Deadline set for \"{task.Title}\"", new BsonDocument { ["taskId"] = task.Id, ["dueDate"] = task.DueDate });
        return Ok(task);
    }
}
