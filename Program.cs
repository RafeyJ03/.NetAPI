using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();


// Global exception handling middleware
app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled exception occurred while processing request.");

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsJsonAsync(new
        {
            error = "Internal server error."
        });
    }
});

// Token authentication middleware
app.UseMiddleware<TokenAuthenticationMiddleware>();

// Request/response logging middleware
app.UseMiddleware<RequestResponseLoggingMiddleware>();

// In-memory data store
var users = new ConcurrentDictionary<int, User>();
var usernameIndex = new ConcurrentDictionary<string, int>(); // Index for fast username lookups
var nextId = 1;

// GET: Retrieve all users with optional pagination
app.MapGet("/users", (int? page, int? size) =>
{
    const int defaultPage = 1;
    const int defaultSize = 10;

    page ??= defaultPage;
    size ??= defaultSize;

    var paginatedUsers = users.Values
        .Skip((page.Value - 1) * size.Value)
        .Take(size.Value);

    return Results.Ok(paginatedUsers);
});

// GET: Retrieve a specific user by ID
app.MapGet("/users/{id:int}", (int id) =>
{
    if (users.TryGetValue(id, out var user))
    {
        return Results.Ok(user);
    }
    return Results.NotFound($"User with ID {id} not found.");
});

// POST: Add a new user
app.MapPost("/users", async (HttpRequest request) =>
{
    User? newUser;
    try
    {
        newUser = await request.ReadFromJsonAsync<User>();
    }
    catch
    {
        return Results.BadRequest(new { Error = "Invalid JSON format. Please check your request body." });
    }

    if (newUser == null)
    {
        return Results.BadRequest(new { Error = "Request body is empty or invalid." });
    }
    if (string.IsNullOrWhiteSpace(newUser.Username))
    {
        return Results.BadRequest(new { Error = "Username is required." });
    }

    if (newUser.UserAge < 0)
    {
        return Results.BadRequest(new { Error = "UserAge must be a non-negative integer." });
    }
    if (usernameIndex.ContainsKey(newUser.Username))
    {
        return Results.BadRequest(new { Error = $"Username '{newUser.Username}' is already taken." });
    }


    newUser.Id = nextId++;
    users[newUser.Id] = newUser;
    usernameIndex[newUser.Username] = newUser.Id;

    return Results.Created($"/users/{newUser.Id}", newUser);
});

// PUT: Update an existing user's details
app.MapPut("/users/{id:int}", async (int id, HttpRequest request) =>
{
    UserUpdate? update;
    try
    {
        update = await request.ReadFromJsonAsync<UserUpdate>();
    }
    catch
    {
        return Results.BadRequest(new { Error = "Invalid JSON format. Please check your request body." });
    }

    if (update == null)
    {
        return Results.BadRequest(new { Error = "Request body is empty or invalid." });
    }

    if (!users.TryGetValue(id, out var existingUser))
    {
        return Results.NotFound($"User with ID {id} not found.");
    }

    // Update only provided fields
    if (!string.IsNullOrWhiteSpace(update.Username))
{
    if (update.Username != existingUser.Username &&
        usernameIndex.ContainsKey(update.Username))
    {
        return Results.BadRequest(new { Error = $"Username '{update.Username}' is already taken." });
    }

    usernameIndex.TryRemove(existingUser.Username, out _);
    existingUser.Username = update.Username;
    usernameIndex[update.Username] = id;
    }

    if (update.UserAge.HasValue)
    {
        if (update.UserAge.Value < 0)
            return Results.BadRequest(new { Error = "UserAge must be a non-negative integer." });

        existingUser.UserAge = update.UserAge.Value;
    }

    users[id] = existingUser;
    return Results.Ok(existingUser);
});


// DELETE: Remove a user by ID
app.MapDelete("/users/{id:int}", (int id) =>
{
    if (users.TryRemove(id, out var removedUser))
    {
        // Remove the username from the usernameIndex
        usernameIndex.TryRemove(removedUser.Username, out _);
        return Results.Ok($"User with ID {id} deleted.");
    }
    return Results.NotFound($"User with ID {id} not found.");
});
app.Run();

record User
{
    public int Id { get; set; }
    required public string Username { get; set; }
    public int UserAge { get; set; }
}

record UserUpdate
{
    public string? Username { get; set; }
    public int? UserAge { get; set; }
}


public class RequestResponseLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestResponseLoggingMiddleware> _logger;

    public RequestResponseLoggingMiddleware(RequestDelegate next, ILogger<RequestResponseLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Log incoming request
        var method = context.Request.Method;
        var path = context.Request.Path;
        _logger.LogInformation("Incoming Request: {Method} {Path}", method, path);

        // Call the next middleware in the pipeline
        await _next(context);

        // Log outgoing response
        var statusCode = context.Response.StatusCode;
        _logger.LogInformation("Outgoing Response: {StatusCode}", statusCode);
    }
}

public class TokenAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TokenAuthenticationMiddleware> _logger;
    private const string TokenHeader = "Authorization";
    private const string ValidToken = "Bearer my-secret-token"; // Replace with your actual token

    public TokenAuthenticationMiddleware(RequestDelegate next, ILogger<TokenAuthenticationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(TokenHeader, out var token) || token != ValidToken)
        {
            _logger.LogWarning("Unauthorized request: missing or invalid token.");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized access. Valid token required." });
            return;
        }

        await _next(context);
    }
}