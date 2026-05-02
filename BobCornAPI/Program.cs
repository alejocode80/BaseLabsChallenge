using System.Net;
using System.Net.Http;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRateLimiter(options =>
{
   options.AddPolicy("OnePerClientPerMinute", context =>
    RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "anonymus",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 1,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

builder.Services.AddAuthentication("ApiKey").AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>("ApiKey", null);
builder.Services.AddAuthorization();

var app = builder.Build();  

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.UseRateLimiter();

var orders = new List<Order>();
var logins = new List<Login>();

//add some records to the logins lists.
PopulateOrders();

var apiKey = builder.Configuration["ApiKey"] ?? "";

void PopulateOrders()
{
    var clientId = GetLocalIpAddress();
    orders.Add(new Order(clientId, 1, DateTime.UtcNow.AddDays(-2))
    {
        OrderNum = Guid.NewGuid().ToString(),
        Status = "purchased",
        ShippedAt = null
    });

    orders.Add(new Order(clientId, 1, DateTime.UtcNow.AddHours(-1))
    {
        OrderNum = Guid.NewGuid().ToString(),
        Status = "shipped",
        ShippedAt = DateTime.UtcNow.AddMinutes(-25)
    });

    orders.Add(new Order(clientId, 1, DateTime.UtcNow.AddDays(-1))
    {
        OrderNum = Guid.NewGuid().ToString(),
        Status = "completed",
        ShippedAt = DateTime.UtcNow.AddHours(-12)
    });

    orders.Add(new Order("190.10.10.60", 1, DateTime.UtcNow.AddHours(-4))
    {
        OrderNum = Guid.NewGuid().ToString(),
        Status = "purchased",
        ShippedAt = null
    });

    orders.Add(new Order("190.10.10.60", 1, DateTime.UtcNow.AddHours(-3))
    {
        OrderNum = Guid.NewGuid().ToString(),
        Status = "shipped",
        ShippedAt = DateTime.UtcNow.AddMinutes(-25)
    });    
}

/// <summary>
/// Retrieves all records from orders list.
/// </summary>
/// <returns>Object with all records and columns</returns>
app.MapGet("/api/orders", () => orders).RequireAuthorization();

app.MapPost("/api/orders", (Order order) =>
{
    var newOrder = order with { OrderNum = Guid.NewGuid().ToString(), TimeStamp = DateTime.UtcNow };
    orders.Add(newOrder);
    return TypedResults.Created("/api/orders/{orderNum}", newOrder);
}).RequireAuthorization().RequireRateLimiting("OnePerClientPerMinute");

app.MapPost("/api/login", async (HttpContext httpContext) =>
{
    httpContext.Request.EnableBuffering();
    using var reader = new System.IO.StreamReader(httpContext.Request.Body);
    var body = await reader.ReadToEndAsync();
    
    var json = System.Text.Json.JsonDocument.Parse(body);
    var secret = json.RootElement.GetProperty("secret").GetString() ?? "";
    
    var isSuccessful = !string.IsNullOrEmpty(secret) && secret.Equals(apiKey);
    var login = new Login(secret, DateTime.UtcNow, isSuccessful);
    logins.Add(login);
    
    if (isSuccessful)
    {
        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        await httpContext.Response.WriteAsJsonAsync(new { message = "Login successful" });
    }
    else
    {
        httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }
});

app.MapGet("/api/logins", () => logins);

app.MapGet("/api/orders/summary", (string clientId) =>
{
    if (string.IsNullOrEmpty(clientId))
    {
        return Results.BadRequest(new { error = "clientId is required" });
    }

    var filteredOrders = orders.Where(o => o.ClientId == clientId).ToList();

    var summary = new
    {
        ClientId = clientId,
        Orders = filteredOrders.Select(o => new
        {
            o.ClientId,
            o.OrderNum,
            o.Amount,
            o.TimeStamp,
            o.Status,
            o.ShippedAt
        }).ToList(),
        PurchasedAmount = filteredOrders.Where(o => o.Status == "purchased").Sum(o => o.Amount),
        ShippedAmount = filteredOrders.Where(o => o.Status == "shipped").Sum(o => o.Amount),
        CompletedAmount = filteredOrders.Where(o => o.Status == "completed").Sum(o => o.Amount)
    };

    return Results.Ok(summary);
}).RequireAuthorization();

/// <summary>
/// Mark one or more orders as shipped
/// </summary>
/// <remarks>
/// Sample request:
/// POST http://localhost:5128/api/shiporder
/// ["785106c2-7c1b-4864-8a04-9d5846d10143"]
/// </remarks>
/// <param name="orderNums">List with numbers of orders</param>
/// <returns>The user details if found.</returns>
/// <response code="200">Return number of orders successfully processed</response>
/// <response code="404">If the order number is not found.</response>
app.MapPost("/api/shiporder", (List<string> orderNums) =>
{
    var errors = new List<string>();
    var successCount = 0;

    foreach (var orderNum in orderNums)
    {
        var order = orders.FirstOrDefault(o => o.OrderNum == orderNum);
        
        if (order == null)
        {
            errors.Add($"Order {orderNum} not found");
            continue;
        }

        if (order.Status != "purchased")
        {
            errors.Add($"Order {orderNum} cannot be shipped. Current status is '{order.Status}'. Only 'purchased' orders can be shipped.");
            continue;
        }

        var updatedOrder = order with { Status = "shipped", ShippedAt = DateTime.UtcNow };
        orders.Remove(order);
        orders.Add(updatedOrder);
        successCount++;
    }

    if (errors.Count > 0 && successCount == 0)
    {
        if (orderNums.Count == 1 && errors[0].Contains("not found"))
        {
            return Results.NotFound(new { error = errors[0] });
        }
        return Results.BadRequest(new { errors });
    }

    return Results.Ok(new { successCount, errors });
}).RequireAuthorization();

app.Run();

static string GetLocalIpAddress()
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var ip = client.GetStringAsync("https://api.ipify.org").GetAwaiter().GetResult();
        return string.IsNullOrWhiteSpace(ip) ? "localhost" : ip.Trim();
    }
    catch
    {
        return "localhost";
    }
}

public record Order(string ClientId, long Amount, DateTime TimeStamp)
{
    public string OrderNum { get; set; } = string.Empty;
    public string Status { get; set; } = "purchased";
    public DateTime? ShippedAt { get; set; }
}

public record Login(string ClientSecret, DateTime TimeStamp, bool IsSuccessful);