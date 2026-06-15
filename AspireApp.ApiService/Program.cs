using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using AspireApp.ApiService;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Add Postgres DbContext via Aspire integration
builder.AddNpgsqlDbContext<TodoDbContext>("mydb");

// Add Redis via Aspire integration
builder.AddRedisClient("cache");

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILogger<Program>>();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Ensure database is created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TodoDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapGet("/", () => "API service is running. Navigate to /todos to see todo items.");

const string CacheKey = "todos:all";

// GET /todos - list all todos (with Redis cache)
app.MapGet("/todos", async (TodoDbContext db, IConnectionMultiplexer redis, ILoggerFactory loggerFactory) =>
{
    var log = loggerFactory.CreateLogger("TodoApi.GetTodos");
    var redisDb = redis.GetDatabase();

    log.LogInformation("[GET /todos] Checking Redis cache for key '{CacheKey}'", CacheKey);
    var cached = await redisDb.StringGetAsync(CacheKey);

    if (!cached.IsNullOrEmpty)
    {
        var cachedString = (string)cached!;
        log.LogInformation("[GET /todos] Cache HIT. Cached data length: {Length} chars", cachedString.Length);
        var result = JsonSerializer.Deserialize<List<TodoItem>>(cachedString);
        log.LogInformation("[GET /todos] Returning {Count} items from cache", result?.Count ?? 0);
        return Results.Json(result);
    }

    log.LogInformation("[GET /todos] Cache MISS. Querying database...");
    var todos = await db.Todos.OrderByDescending(t => t.CreatedAt).ToListAsync();
    log.LogInformation("[GET /todos] Database returned {Count} items", todos.Count);

    var json = JsonSerializer.Serialize(todos);
    var cacheSet = await redisDb.StringSetAsync(CacheKey, json, TimeSpan.FromSeconds(60));
    log.LogInformation("[GET /todos] Cache SET result: {Result}, data length: {Length} chars, TTL: 60s", cacheSet, json.Length);

    return Results.Json(todos);
}).WithName("GetTodos");

// GET /todos/{id} - get single todo
app.MapGet("/todos/{id}", async (int id, TodoDbContext db) =>
{
    var todo = await db.Todos.FindAsync(id);
    return todo is null ? Results.NotFound() : Results.Json(todo);
}).WithName("GetTodoById");

// POST /todos - create todo (write-through cache)
app.MapPost("/todos", async (TodoItem todo, TodoDbContext db, IConnectionMultiplexer redis, ILoggerFactory loggerFactory) =>
{
    var log = loggerFactory.CreateLogger("TodoApi.CreateTodo");

    todo.CreatedAt = DateTime.UtcNow;
    db.Todos.Add(todo);
    await db.SaveChangesAsync();
    log.LogInformation("[POST /todos] Created todo item: Id={Id}, Title='{Title}'", todo.Id, todo.Title);

    // Write-through: re-query DB and update cache immediately
    var allTodos = await db.Todos.OrderByDescending(t => t.CreatedAt).ToListAsync();
    var json = JsonSerializer.Serialize(allTodos);
    var redisDb = redis.GetDatabase();
    var cacheSet = await redisDb.StringSetAsync(CacheKey, json, TimeSpan.FromSeconds(60));
    log.LogInformation("[POST /todos] Cache updated: SET result={Result}, {Count} items cached, TTL: 60s", cacheSet, allTodos.Count);

    return Results.Created($"/todos/{todo.Id}", todo);
}).WithName("CreateTodo");

// PUT /todos/{id} - update todo (write-through cache)
app.MapPut("/todos/{id}", async (int id, TodoItem input, TodoDbContext db, IConnectionMultiplexer redis, ILoggerFactory loggerFactory) =>
{
    var log = loggerFactory.CreateLogger("TodoApi.UpdateTodo");

    var todo = await db.Todos.FindAsync(id);
    if (todo is null)
    {
        log.LogWarning("[PUT /todos/{Id}] Todo not found in database", id);
        return Results.NotFound();
    }

    todo.Title = input.Title;
    todo.IsComplete = input.IsComplete;
    await db.SaveChangesAsync();
    log.LogInformation("[PUT /todos/{Id}] Updated: Title='{Title}', IsComplete={IsComplete}", id, todo.Title, todo.IsComplete);

    // Write-through: re-query DB and update cache immediately
    var allTodos = await db.Todos.OrderByDescending(t => t.CreatedAt).ToListAsync();
    var json = JsonSerializer.Serialize(allTodos);
    var redisDb = redis.GetDatabase();
    var cacheSet = await redisDb.StringSetAsync(CacheKey, json, TimeSpan.FromSeconds(60));
    log.LogInformation("[PUT /todos/{Id}] Cache updated: SET result={Result}, {Count} items cached", id, cacheSet, allTodos.Count);

    return Results.Json(todo);
}).WithName("UpdateTodo");

// DELETE /todos/{id} - delete todo (write-through cache)
app.MapDelete("/todos/{id}", async (int id, TodoDbContext db, IConnectionMultiplexer redis, ILoggerFactory loggerFactory) =>
{
    var log = loggerFactory.CreateLogger("TodoApi.DeleteTodo");

    var todo = await db.Todos.FindAsync(id);
    if (todo is null)
    {
        log.LogWarning("[DELETE /todos/{Id}] Todo not found in database", id);
        return Results.NoContent();
    }

    db.Todos.Remove(todo);
    await db.SaveChangesAsync();
    log.LogInformation("[DELETE /todos/{Id}] Deleted: Title='{Title}'", id, todo.Title);

    // Write-through: re-query DB and update cache immediately
    var allTodos = await db.Todos.OrderByDescending(t => t.CreatedAt).ToListAsync();
    var json = JsonSerializer.Serialize(allTodos);
    var redisDb = redis.GetDatabase();
    var cacheSet = await redisDb.StringSetAsync(CacheKey, json, TimeSpan.FromSeconds(60));
    log.LogInformation("[DELETE /todos/{Id}] Cache updated: SET result={Result}, {Count} items remaining", id, cacheSet, allTodos.Count);

    return Results.NoContent();
}).WithName("DeleteTodo");

string[] summaries = ["Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"];

app.MapGet("/weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.MapDefaultEndpoints();

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
