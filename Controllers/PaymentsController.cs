using System.Text.Json;
using aspbackend.Data;
using aspbackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;

namespace aspbackend.Controllers;

[ApiController]
[Route("api/payments")]
public sealed class PaymentsController(MongoDbContext db, WaafiPayService waafi, NotificationService notifications, IConfiguration config) : BaseApiController(db)
{
    public record CheckoutDto(string Plan, string? PaymentMethod, string? AccountNo, JsonElement? PayerInfo, decimal? Amount);

    [HttpGet("plans")]
    public IActionResult Plans() => Ok(waafi.GetPlanCatalog());

    [Authorize]
    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout(CheckoutDto dto)
    {
        var user = await RequireUser();
        if (user is null)
        {
            return User.Identity?.IsAuthenticated == true
                ? Unauthorized(new { message = "Session expired or account not found. Please log out and sign in again." })
                : Unauthorized(new { message = "Not authorized" });
        }
        if (dto.Plan is not ("pro" or "team")) return BadRequest(new { message = "plan must be pro or team" });
        if (string.IsNullOrWhiteSpace(dto.PaymentMethod))
            return BadRequest(new { message = "paymentMethod is required (see WaafiPay docs, e.g. MWALLET_ACCOUNT, CREDIT_CARD)" });

        var accountNo = dto.AccountNo;
        if (string.IsNullOrWhiteSpace(accountNo) && dto.PayerInfo is { } payer)
        {
            if (payer.TryGetProperty("accountNo", out var a)) accountNo = a.GetString();
            else if (payer.TryGetProperty("accountNumber", out var n)) accountNo = n.GetString();
        }

        if (string.IsNullOrWhiteSpace(accountNo))
            return BadRequest(new { message = "accountNo is required (mobile wallet, e.g. 252611111111)" });

        try
        {
            var result = await waafi.CheckoutAsync(
                user,
                dto.Plan,
                dto.PaymentMethod ?? "MWALLET_ACCOUNT",
                accountNo,
                dto.Amount);

            if (result.SubscriptionUpdated)
            {
                await notifications.NotifyAsync(user.Id, "payment", "Your WaafiPay payment was applied and your plan is updated.", new BsonDocument { ["referenceId"] = result.ReferenceId });
            }

            return Ok(new
            {
                provider = "waafipay",
                referenceId = result.ReferenceId,
                amount = result.Amount,
                currency = result.Currency,
                waafiResponse = result.Data,
                subscriptionUpdated = result.SubscriptionUpdated
            });
        }
        catch (WaafiPayException ex)
        {
            return StatusCode(402, new { message = ex.Message, waafiResponse = ex.WaafiResponse });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { message = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("portal")]
    public IActionResult Portal()
    {
        var url = config["WAAFIPAY_PORTAL_URL"] ?? "";
        return Ok(new
        {
            provider = "waafipay",
            url = string.IsNullOrWhiteSpace(url) ? null : url,
            message = string.IsNullOrWhiteSpace(url) ? "Set WAAFIPAY_PORTAL_URL to your Waafi merchant or customer portal link." : null
        });
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook([FromBody] JsonElement body)
    {
        var secret = config["WAAFIPAY_WEBHOOK_SECRET"];
        if (!string.IsNullOrWhiteSpace(secret))
        {
            var got = Request.Headers["x-waafipay-signature"].FirstOrDefault() ?? Request.Headers["x-webhook-secret"].FirstOrDefault();
            if (got != secret) return Unauthorized(new { message = "Invalid webhook signature" });
        }

        var result = await waafi.HandleWebhookAsync(body);
        if (result.Applied)
        {
            var referenceId = WaafiPayService.ParseReferenceId(
                body.TryGetProperty("referenceId", out var r) ? r.GetString() : null);
            if (referenceId is not null)
            {
                await notifications.NotifyAsync(referenceId.Value.UserId, "payment", "Your subscription was updated via WaafiPay.", new BsonDocument { ["webhook"] = true });
            }
        }

        return Ok(new { received = true, applied = result.Applied, reason = result.Reason });
    }
}
