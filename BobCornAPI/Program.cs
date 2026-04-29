using System.Threading.RateLimiting;

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

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRateLimiter();

var orders = new List<Order>();

app.MapGet("/api/orders", () => orders);

app.MapPost("/api/orders", (Order order) =>
{
    var newOrder = order with { OrderNum = Guid.NewGuid().ToString(), TimeStamp = DateTime.UtcNow };
    orders.Add(newOrder);
    return TypedResults.Created("/api/orders/{orderNum}", newOrder);
}).RequireRateLimiting("OnePerClientPerMinute");

app.Run();

public record Order(string CliendId, long Amount, DateTime TimeStamp)
{
    public string OrderNum { get; set; } = string.Empty;
}

