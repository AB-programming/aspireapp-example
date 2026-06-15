using System.Net.Http.Json;

namespace AspireApp.Web;

public class TodoApiClient(HttpClient httpClient)
{
    public async Task<TodoItem[]> GetTodosAsync(CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<TodoItem[]>("/todos", cancellationToken) ?? [];
    }

    public async Task<TodoItem?> CreateTodoAsync(string title, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync("/todos", new TodoItem { Title = title }, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TodoItem>(cancellationToken: cancellationToken);
    }

    public async Task ToggleTodoAsync(TodoItem todo, CancellationToken cancellationToken = default)
    {
        await httpClient.PutAsJsonAsync($"/todos/{todo.Id}", new TodoItem
        {
            Id = todo.Id,
            Title = todo.Title,
            IsComplete = !todo.IsComplete,
            CreatedAt = todo.CreatedAt
        }, cancellationToken);
    }

    public async Task DeleteTodoAsync(int id, CancellationToken cancellationToken = default)
    {
        await httpClient.DeleteAsync($"/todos/{id}", cancellationToken);
    }
}

public class TodoItem
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool IsComplete { get; set; }
    public DateTime CreatedAt { get; set; }
}
