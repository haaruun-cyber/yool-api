using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using aspbackend.Data;
using aspbackend.Models;
using MongoDB.Bson;

namespace aspbackend.Services;

public sealed class WaafiPayException(string message, JsonElement? waafiResponse = null) : Exception(message)
{
    public JsonElement? WaafiResponse { get; } = waafiResponse;
}

public sealed class WaafiPayService(MongoDbContext db, IConfiguration configuration, ILogger<WaafiPayService> logger)
{
    private static readonly HttpClient Http = new();
    private static readonly Regex ReferencePattern = new(@"^yool_([a-f0-9]{24})_(pro|team)_(\d+)$", RegexOptions.IgnoreCase);

    public object GetPlanCatalog() => new
    {
        currency = Currency(),
        pro = new { amount = PlanAmount("pro"), plan = "pro" },
        team = new { amount = PlanAmount("team"), plan = "team" }
    };

    public async Task<CheckoutResult> CheckoutAsync(
        User user,
        string plan,
        string paymentMethod,
        string accountNo,
        decimal? amountOverride,
        CancellationToken cancellationToken = default)
    {
        if (plan is not ("pro" or "team")) throw new ArgumentException("Invalid plan");
        var amount = amountOverride ?? PlanAmount(plan);
        var referenceId = BuildReferenceId(user.Id, plan);
        var description = $"Yool {plan} subscription";
        var digits = Regex.Replace(accountNo, @"\D", "");

        JsonElement data;
        try
        {
            data = await PurchaseAsync(amount, Currency(), paymentMethod, digits, description, cancellationToken);
        }
        catch (WaafiPayException ex)
        {
            throw new WaafiPayException(ex.Message, ex.WaafiResponse);
        }

        var subscriptionUpdated = await ApplyPaidPlanAsync(user.Id, plan, referenceId, new BsonDocument
        {
            ["gatewayResponse"] = BsonDocument.Parse(data.GetRawText()),
            ["transactionId"] = ExtractTransactionId(data) ?? ""
        }, cancellationToken);

        return new CheckoutResult(referenceId, amount, Currency(), data, subscriptionUpdated);
    }

    public async Task<WebhookResult> HandleWebhookAsync(JsonElement body, CancellationToken cancellationToken = default)
    {
        var flat = FlattenWebhook(body);
        var referenceId = FindReferenceId(flat, body);
        var parsed = ParseReferenceId(referenceId);
        if (parsed is null) return new WebhookResult(false, "Unrecognized referenceId");
        if (!IsLikelySuccessWebhook(flat, body)) return new WebhookResult(false, "Not a success payload");
        var applied = await ApplyPaidPlanAsync(parsed.Value.UserId, parsed.Value.Plan, referenceId!, new BsonDocument { ["webhook"] = BsonDocument.Parse(body.GetRawText()) }, cancellationToken);
        return new WebhookResult(applied, applied ? null : "Apply failed");
    }

    public static ParsedReference? ParseReferenceId(string? referenceId)
    {
        if (string.IsNullOrWhiteSpace(referenceId)) return null;
        var m = ReferencePattern.Match(referenceId.Trim());
        if (!m.Success) return null;
        if (!ObjectId.TryParse(m.Groups[1].Value, out var userId)) return null;
        return new ParsedReference(userId, m.Groups[2].Value.ToLowerInvariant());
    }

    private async Task<JsonElement> PurchaseAsync(
        decimal amount,
        string currency,
        string paymentMethod,
        string accountNo,
        string description,
        CancellationToken cancellationToken)
    {
        var preAuth = await WaafiCallAsync("API_PREAUTHORIZE", new Dictionary<string, object?>
        {
            ["paymentMethod"] = paymentMethod,
            ["payerInfo"] = new Dictionary<string, object> { ["accountNo"] = accountNo },
            ["transactionInfo"] = new Dictionary<string, object>
            {
                ["amount"] = amount.ToString("F2", CultureInfo.InvariantCulture),
                ["currency"] = currency,
                ["description"] = description
            }
        }, cancellationToken);

        var transactionId = ExtractTransactionId(preAuth);
        if (!IsPaymentSuccess(preAuth))
        {
            if (!string.IsNullOrWhiteSpace(transactionId))
            {
                try
                {
                    await WaafiCallAsync("API_PREAUTHORIZE_CANCEL", new Dictionary<string, object?>
                    {
                        ["transactionId"] = transactionId,
                        ["description"] = "Payment cancelled"
                    }, cancellationToken);
                }
                catch { /* ignore */ }
            }

            throw new WaafiPayException(GetErrorMessage(preAuth), preAuth);
        }

        if (string.IsNullOrWhiteSpace(transactionId))
            throw new WaafiPayException("WaafiPay did not return a transactionId", preAuth);

        var commit = await WaafiCallAsync("API_PREAUTHORIZE_COMMIT", new Dictionary<string, object?>
        {
            ["transactionId"] = transactionId,
            ["description"] = $"{description} committed"
        }, cancellationToken);

        if (!IsPaymentSuccess(commit) && !IsLikelySuccess(commit))
        {
            try
            {
                await WaafiCallAsync("API_PREAUTHORIZE_CANCEL", new Dictionary<string, object?>
                {
                    ["transactionId"] = transactionId,
                    ["description"] = "Commit failed — cancelled"
                }, cancellationToken);
            }
            catch { /* ignore */ }

            throw new WaafiPayException(GetErrorMessage(commit), commit);
        }

        return commit;
    }

    private async Task<JsonElement> WaafiCallAsync(string serviceName, Dictionary<string, object?> serviceParams, CancellationToken cancellationToken)
    {
        var creds = GetCredentials();
        serviceParams["merchantUid"] = creds.MerchantUid;
        serviceParams["apiUserId"] = long.TryParse(creds.ApiUserId, out var apiUserId) ? apiUserId : creds.ApiUserId;
        serviceParams["apiKey"] = creds.ApiKey;

        var referenceId = Random.Shared.Next(100000, 999999).ToString();
        var invoiceId = Random.Shared.Next(100000, 999999).ToString();
        if (serviceParams.TryGetValue("transactionInfo", out var txObj) && txObj is Dictionary<string, object> tx)
        {
            tx["referenceId"] = referenceId;
            tx["invoiceId"] = invoiceId;
        }

        var payload = new Dictionary<string, object?>
        {
            ["schemaVersion"] = "1.0",
            ["requestId"] = Guid.NewGuid().ToString(),
            ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            ["channelName"] = "WEB",
            ["serviceName"] = serviceName,
            ["serviceParams"] = serviceParams
        };

        var baseUrl = GetBaseUrl();
        logger.LogDebug("WaafiPay {Service} → {BaseUrl}", serviceName, baseUrl);
        var response = await Http.PostAsJsonAsync(baseUrl, payload, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
            throw new WaafiPayException("WaafiPay returned an empty response");

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private async Task<bool> ApplyPaidPlanAsync(ObjectId userId, string plan, string referenceId, BsonDocument metadata, CancellationToken cancellationToken)
    {
        var user = await db.GetAsync<User>(Tables.Users, userId);
        if (user is null) return false;

        user.SubscriptionPlan = plan == "team" ? "team" : "pro";
        user.UpdatedAt = DateTime.UtcNow;
        await db.ReplaceAsync(Tables.Users, user.Id, user);

        var subscriptions = await db.AllAsync<Subscription>(Tables.Subscriptions);
        var subscription = subscriptions.FirstOrDefault(x => x.UserId == userId && x.Provider == "waafipay")
            ?? new Subscription { Id = ObjectId.GenerateNewId(), UserId = userId, Provider = "waafipay", CreatedAt = DateTime.UtcNow };

        subscription.Plan = user.SubscriptionPlan;
        subscription.Status = "active";
        subscription.WaafiReferenceId = referenceId;
        subscription.CurrentPeriodEnd = DateTime.UtcNow.AddDays(30);
        subscription.Metadata = metadata;
        subscription.UpdatedAt = DateTime.UtcNow;
        await db.ReplaceAsync(Tables.Subscriptions, subscription.Id, subscription);
        return true;
    }

    private (string ApiKey, string ApiUserId, string MerchantUid) GetCredentials()
    {
        var apiKey = configuration["WAAFIPAY_API_KEY"] ?? configuration["API_KEY"];
        var apiUserId = configuration["WAAFIPAY_API_USER_ID"] ?? configuration["STORE_ID"];
        var merchantUid = configuration["WAAFIPAY_MERCHANT_UID"] ?? configuration["MERCHANTUID"];
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiUserId) || string.IsNullOrWhiteSpace(merchantUid))
        {
            throw new InvalidOperationException(
                "WaafiPay is not configured. Set WAAFIPAY_API_KEY (or API_KEY), WAAFIPAY_API_USER_ID (or STORE_ID), WAAFIPAY_MERCHANT_UID (or MERCHANTUID).");
        }

        return (apiKey, apiUserId, merchantUid);
    }

    private string GetBaseUrl()
    {
        var overrideUrl = configuration["WAAFIPAY_BASE_URL"];
        if (!string.IsNullOrWhiteSpace(overrideUrl)) return overrideUrl.TrimEnd('/');

        // Match waafipay-sdk-node: WAAFIPAY_TEST_MODE=true → production, false → sandbox
        var testMode = configuration["WAAFIPAY_TEST_MODE"] is not "false";
        return testMode
            ? "https://api.waafipay.net/asm"
            : "https://sandbox.waafipay.net/asm";
    }

    private string Currency() => configuration["WAAFIPAY_CURRENCY"] ?? "USD";

    private decimal PlanAmount(string plan) =>
        plan == "team"
            ? decimal.Parse(configuration["WAAFIPAY_TEAM_AMOUNT"] ?? "24")
            : decimal.Parse(configuration["WAAFIPAY_PRO_AMOUNT"] ?? "12");

    private static string BuildReferenceId(ObjectId userId, string plan) =>
        $"yool_{userId}_{plan}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

    private static string? ExtractTransactionId(JsonElement data)
    {
        if (data.TryGetProperty("params", out var p) && p.TryGetProperty("transactionId", out var tid))
            return tid.GetString();
        if (data.TryGetProperty("transactionId", out var direct))
            return direct.GetString();
        return null;
    }

    private static string? GetErrorCode(JsonElement data)
    {
        if (data.TryGetProperty("errorCode", out var code))
            return code.ToString();
        if (data.TryGetProperty("responseMsg", out var msg) && msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("errorCode", out var nested))
            return nested.ToString();
        return null;
    }

    private static bool IsPaymentSuccess(JsonElement data) => GetErrorCode(data) == "0";

    private static bool IsLikelySuccess(JsonElement data)
    {
        if (IsPaymentSuccess(data)) return true;
        foreach (var key in new[] { "state", "status", "transactionStatus" })
        {
            if (!data.TryGetProperty(key, out var prop)) continue;
            var state = prop.GetString()?.ToUpperInvariant();
            if (state is "SUCCESS" or "APPROVED" or "COMPLETED" or "PAID") return true;
        }

        return false;
    }

    private static bool IsLikelySuccessWebhook(Dictionary<string, JsonElement> flat, JsonElement body)
    {
        if (IsLikelySuccess(body)) return true;
        foreach (var key in new[] { "errorCode", "state", "status", "transactionStatus" })
        {
            if (!flat.TryGetValue(key, out var prop)) continue;
            if (key == "errorCode" && prop.ToString() == "0") return true;
            var state = prop.GetString()?.ToUpperInvariant();
            if (state is "SUCCESS" or "APPROVED" or "COMPLETED" or "PAID") return true;
        }

        return false;
    }

    private static string GetErrorMessage(JsonElement data)
    {
        if (data.TryGetProperty("responseMsg", out var msg))
        {
            if (msg.ValueKind == JsonValueKind.String)
            {
                var text = msg.GetString();
                if (!string.IsNullOrWhiteSpace(text) && text != "RCS_SUCCESS") return text;
            }
            else if (msg.ValueKind == JsonValueKind.Object)
            {
                if (msg.TryGetProperty("message", out var nested)) return nested.GetString() ?? "WaafiPay request failed";
                if (msg.TryGetProperty("description", out var desc)) return desc.GetString() ?? "WaafiPay request failed";
            }
        }

        if (data.TryGetProperty("message", out var m)) return m.GetString() ?? "WaafiPay request failed";
        if (data.TryGetProperty("responseCode", out var code) && data.TryGetProperty("errorCode", out var err))
            return $"WaafiPay error (responseCode={code}, errorCode={err})";
        return "WaafiPay request failed";
    }

    private static Dictionary<string, JsonElement> FlattenWebhook(JsonElement body)
    {
        var flat = new Dictionary<string, JsonElement>();
        if (body.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in body.EnumerateObject())
                flat[prop.Name] = prop.Value;
        }

        if (body.TryGetProperty("serviceParams", out var sp) && sp.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in sp.EnumerateObject())
                flat[prop.Name] = prop.Value;
        }

        return flat;
    }

    private static string? FindReferenceId(Dictionary<string, JsonElement> flat, JsonElement body)
    {
        foreach (var key in new[] { "referenceId" })
        {
            if (flat.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }

        if (flat.TryGetValue("transactionInfo", out var ti) && ti.TryGetProperty("referenceId", out var refInTi))
            return refInTi.GetString();
        if (body.TryGetProperty("transactionInfo", out var bodyTi) && bodyTi.TryGetProperty("referenceId", out var refBody))
            return refBody.GetString();
        return null;
    }

    public readonly record struct CheckoutResult(string ReferenceId, decimal Amount, string Currency, JsonElement Data, bool SubscriptionUpdated);
    public readonly record struct WebhookResult(bool Applied, string? Reason);
    public readonly record struct ParsedReference(ObjectId UserId, string Plan);
}
