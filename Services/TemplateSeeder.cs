using aspbackend.Data;
using aspbackend.Models;
using MongoDB.Bson;

namespace aspbackend.Services;

public static class TemplateSeeder
{
    private static readonly (string Title, string Category, bool IsPremium)[] Catalog =
    [
        ("Weekly To-do List", "todo_list", false),
        ("Project Planner", "project_planner", false),
        ("Habit Tracker", "habit_tracker", false),
        ("Meal Planner", "meal_planner", false),
        ("Journal", "journal", false),
        ("Reading List", "reading_list", false),
        ("Travel Planner", "travel_planner", true),
        ("Monthly Budget", "budget_planner", true),
        ("Quick note", "note", false),
    ];

    public static async Task SeedAsync(MongoDbContext db, ILogger logger, CancellationToken cancellationToken = default)
    {
        var existing = await db.AllAsync<Template>(Tables.Templates);
        var byTitle = existing.ToDictionary(t => t.Title, StringComparer.OrdinalIgnoreCase);
        var seeded = 0;
        var now = DateTime.UtcNow;

        foreach (var (title, category, isPremium) in Catalog)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = TemplateDefaults.GetDefaultContent(category);

            if (byTitle.TryGetValue(title, out var tpl))
            {
                tpl.Category = category;
                tpl.Content = content;
                tpl.IsPremium = isPremium;
                tpl.IsActive = true;
                tpl.UpdatedAt = now;
                await db.ReplaceAsync(Tables.Templates, tpl.Id, tpl);
            }
            else
            {
                tpl = new Template
                {
                    Id = ObjectId.GenerateNewId(),
                    Title = title,
                    Category = category,
                    Content = content,
                    IsPremium = isPremium,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                await db.InsertAsync(Tables.Templates, tpl.Id, tpl);
                byTitle[title] = tpl;
                seeded++;
            }
        }

        logger.LogInformation("Template library ready ({Total} templates, {New} new)", Catalog.Length, seeded);
    }
}

internal static class TemplateDefaults
{
    private static string Uid() => $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}"[..20];

    public static BsonDocument GetDefaultContent(string type) => type switch
    {
        "todo_list" => new BsonDocument
        {
            ["view"] = "weekly_todo",
            ["weekLabel"] = "This week",
            ["days"] = new BsonDocument { ["mon"] = new BsonArray { TodoItem("To-do", false) } }
        },
        "meal_planner" => new BsonDocument
        {
            ["view"] = "meal_planner",
            ["weeklyPlan"] = new BsonDocument(),
            ["meals"] = new BsonArray()
        },
        "travel_planner" => new BsonDocument
        {
            ["view"] = "travel_planner",
            ["packing"] = new BsonDocument
            {
                ["clothing"] = new BsonArray(),
                ["toiletries"] = new BsonArray()
            },
            ["itinerary"] = new BsonArray()
        },
        "budget_planner" => new BsonDocument
        {
            ["view"] = "budget_planner",
            ["income"] = new BsonArray { IncomeRow("Salary", 3500) },
            ["expenses"] = new BsonArray { ExpenseRow("Rent", 1200) }
        },
        "journal" => new BsonDocument { ["view"] = "journal", ["entries"] = new BsonArray() },
        "habit_tracker" => new BsonDocument
        {
            ["view"] = "habit_tracker",
            ["columns"] = new BsonDocument
            {
                ["not_started"] = new BsonArray(),
                ["in_progress"] = new BsonArray(),
                ["done"] = new BsonArray()
            }
        },
        "project_planner" => new BsonDocument { ["view"] = "project_planner", ["rows"] = new BsonArray() },
        "reading_list" => new BsonDocument { ["view"] = "reading_list", ["books"] = new BsonArray() },
        _ => new BsonDocument { ["view"] = "note" }
    };

    private static BsonDocument TodoItem(string text, bool done) =>
        new() { ["id"] = Uid(), ["text"] = text, ["done"] = done };

    private static BsonDocument IncomeRow(string item, decimal amount) =>
        new() { ["id"] = Uid(), ["item"] = item, ["amount"] = amount };

    private static BsonDocument ExpenseRow(string item, decimal amount) =>
        new() { ["id"] = Uid(), ["item"] = item, ["amount"] = amount };
}
