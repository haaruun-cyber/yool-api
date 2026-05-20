using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace aspbackend.Models;

public sealed class RefreshTokenRecord
{
    [BsonElement("hash")] public string Hash { get; set; } = "";
    [BsonElement("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class User
{
    [BsonId, JsonPropertyName("_id")] public ObjectId Id { get; set; }
    [BsonElement("name")] public string Name { get; set; } = "";
    [BsonElement("email")] public string Email { get; set; } = "";
    [BsonElement("password")] public string? Password { get; set; }
    [BsonElement("avatar")] public string Avatar { get; set; } = "";
    [BsonElement("role")] public string Role { get; set; } = "user";
    [BsonElement("subscriptionPlan")] public string SubscriptionPlan { get; set; } = "free";
    [BsonElement("emailVerified")] public bool EmailVerified { get; set; }
    [BsonElement("googleId")] public string? GoogleId { get; set; }
    [BsonElement("isBlocked")] public bool IsBlocked { get; set; }
    [BsonElement("refreshTokens")] public List<RefreshTokenRecord> RefreshTokens { get; set; } = [];
    [BsonElement("emailVerificationToken")] public string? EmailVerificationToken { get; set; }
    [BsonElement("emailVerificationExpires")] public DateTime? EmailVerificationExpires { get; set; }
    [BsonElement("passwordResetToken")] public string? PasswordResetToken { get; set; }
    [BsonElement("passwordResetExpires")] public DateTime? PasswordResetExpires { get; set; }
    [BsonElement("lastLoginAt")] public DateTime? LastLoginAt { get; set; }
    [BsonElement("loginCount")] public int LoginCount { get; set; }
    [BsonElement("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [BsonElement("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class DocumentShare
{
    [BsonElement("user")] public ObjectId User { get; set; }
    [BsonElement("permission")] public string Permission { get; set; } = "read";
}

public sealed class PendingInvite
{
    [BsonElement("email")] public string Email { get; set; } = "";
    [BsonElement("permission")] public string Permission { get; set; } = "read";
}

public sealed class Document
{
    public static readonly string[] DocumentTypes = ["note", "journal", "habit_tracker", "meal_planner", "travel_planner", "budget_planner", "reading_list", "todo_list", "project_planner"];

    [BsonId, JsonPropertyName("_id")] public ObjectId Id { get; set; }
    [BsonElement("title")] public string Title { get; set; } = "";
    [BsonElement("content")] public BsonValue Content { get; set; } = "";
    [BsonElement("type")] public string Type { get; set; } = "note";
    [BsonElement("ownerId")] public ObjectId OwnerId { get; set; }
    [BsonElement("isPrivate")] public bool IsPrivate { get; set; } = true;
    [BsonElement("isFavorite")] public bool IsFavorite { get; set; }
    [BsonElement("isArchived")] public bool IsArchived { get; set; }
    [BsonElement("isPinned")] public bool IsPinned { get; set; }
    [BsonElement("sharedUsers")] public List<DocumentShare> SharedUsers { get; set; } = [];
    [BsonElement("pendingInvites")] public List<PendingInvite> PendingInvites { get; set; } = [];
    [BsonElement("coverImage")] public string CoverImage { get; set; } = "";
    [BsonElement("status")] public string Status { get; set; } = "not_started";
    [BsonElement("deadline")] public DateTime? Deadline { get; set; }
    [BsonElement("tags")] public List<string> Tags { get; set; } = [];
    [BsonElement("lastEdited")] public DateTime LastEdited { get; set; } = DateTime.UtcNow;
    [BsonElement("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [BsonElement("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class DocumentVersion
{
    [BsonId, JsonPropertyName("_id")] public ObjectId Id { get; set; }
    [BsonElement("documentId")] public ObjectId DocumentId { get; set; }
    [BsonElement("title")] public string Title { get; set; } = "";
    [BsonElement("content")] public BsonValue Content { get; set; } = "";
    [BsonElement("createdBy")] public ObjectId CreatedBy { get; set; }
    [BsonElement("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [BsonElement("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class TaskItem
{
    [BsonId, JsonPropertyName("_id")] public ObjectId Id { get; set; }
    [BsonElement("title")] public string Title { get; set; } = "";
    [BsonElement("description")] public string Description { get; set; } = "";
    [BsonElement("status")] public string Status { get; set; } = "open";
    [BsonElement("dueDate")] public DateTime? DueDate { get; set; }
    [BsonElement("priority")] public string Priority { get; set; } = "medium";
    [BsonElement("documentId")] public ObjectId DocumentId { get; set; }
    [BsonElement("userId")] public ObjectId UserId { get; set; }
    [BsonElement("completedAt")] public DateTime? CompletedAt { get; set; }
    [BsonElement("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [BsonElement("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Template
{
    [BsonId, JsonPropertyName("_id")] public ObjectId Id { get; set; }
    [BsonElement("title")] public string Title { get; set; } = "";
    [BsonElement("category")] public string Category { get; set; } = "";
    [BsonElement("content")] public BsonValue Content { get; set; } = new BsonDocument();
    [BsonElement("isPremium")] public bool IsPremium { get; set; }
    [BsonElement("isActive")] public bool IsActive { get; set; } = true;
    [BsonElement("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [BsonElement("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Notification
{
    [BsonId, JsonPropertyName("_id")] public ObjectId Id { get; set; }
    [BsonElement("userId")] public ObjectId UserId { get; set; }
    [BsonElement("type")] public string Type { get; set; } = "system";
    [BsonElement("message")] public string Message { get; set; } = "";
    [BsonElement("isRead")] public bool IsRead { get; set; }
    [BsonElement("metadata")] public BsonValue? Metadata { get; set; }
    [BsonElement("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [BsonElement("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Subscription
{
    [BsonId, JsonPropertyName("_id")] public ObjectId Id { get; set; }
    [BsonElement("userId")] public ObjectId UserId { get; set; }
    [BsonElement("provider")] public string Provider { get; set; } = "waafipay";
    [BsonElement("plan")] public string Plan { get; set; } = "free";
    [BsonElement("status")] public string Status { get; set; } = "incomplete";
    [BsonElement("currentPeriodEnd")] public DateTime? CurrentPeriodEnd { get; set; }
    [BsonElement("cancelAtPeriodEnd")] public bool CancelAtPeriodEnd { get; set; }
    [BsonElement("waafiReferenceId")] public string? WaafiReferenceId { get; set; }
    [BsonElement("metadata")] public BsonValue? Metadata { get; set; }
    [BsonElement("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [BsonElement("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class AnalyticsDaily
{
    [BsonId, JsonPropertyName("_id")] public ObjectId Id { get; set; }
    [BsonElement("date")] public string Date { get; set; } = "";
    [BsonElement("activeUsers")] public int ActiveUsers { get; set; }
    [BsonElement("logins")] public int Logins { get; set; }
    [BsonElement("documentsCreated")] public int DocumentsCreated { get; set; }
    [BsonElement("paidUsers")] public int PaidUsers { get; set; }
    [BsonElement("churned")] public int Churned { get; set; }
    [BsonElement("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [BsonElement("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class AiHistory
{
    [BsonId, JsonPropertyName("_id")] public ObjectId Id { get; set; }
    [BsonElement("userId")] public ObjectId UserId { get; set; }
    [BsonElement("type")] public string Type { get; set; } = "";
    [BsonElement("prompt")] public string Prompt { get; set; } = "";
    [BsonElement("response")] public string Response { get; set; } = "";
    [BsonElement("metadata")] public BsonValue? Metadata { get; set; }
    [BsonElement("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [BsonElement("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
