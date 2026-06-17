using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Npgsql;

// DB
var db =
    "Host=localhost;Port=5432;Database=webserver;Username=postgres;Password=password";
var dataSource = NpgsqlDataSource.Create(db);
var UserRepository = new UserRepository(dataSource);

// Routes => (status, type, body)
var routes = new Dictionary<(string Method, string Path), 
    Func<string, Dictionary<string, string>, Task<(int Status, string ContentType, string Body)>>>();

routes[("GET", "/hello")] = async (body, routeParams) => (200, "text/plain", "Hello dude!");

routes[("POST", "/echo")] = async (body, routeParams) =>
{
    try
    {
        // JSON Parser
        var parsed = JsonSerializer.Deserialize<EchoRequest>(body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (parsed is null) return (400, "text/plain", "Invalid JSON body");
        return (200, "application/json", JsonSerializer.Serialize(new { received = parsed.Message }));
    }
    catch (JsonException) { return (400, "text/plain", "Invalid JSON body"); }
};

routes[("GET", "/users")] = async (body, routeParams) =>
{
    var users = await UserRepository.GetAllUsers();

    return (200, "application/json", JsonSerializer.Serialize(users));
};

routes[("GET", "/users/{id}")] = async (body, routeParams) =>
{
    if (!int.TryParse(routeParams["id"], out int id))
        return (400, "text/plain", "Invalid user id");

    var found = await UserRepository.GetUserById(id);

    if (found is null)
        return (404, "text/plain", "User not found");

    return (200, "application/json", JsonSerializer.Serialize(found));
};

routes[("POST", "/users")] = async (body, routeParams) =>
{
    try
    {
        var parsed = JsonSerializer.Deserialize<RegisterRequest>(body, 
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (parsed is null) return (400, "text/plain", "Invalid JSON body");
        
        var user = await UserRepository.CreateUser(parsed.Name, parsed.Email);

        return (201, "application/json", $"User ID : {user} registered");
        
    } 
    catch (JsonException) { return (400, "text/plain", "Invalid JSON body"); }
    catch (PostgresException ex) when (ex.SqlState == "23505") { return (409, "text/plain", "Email already in use"); }
};

routes[("PUT", "/users/{id}")] = async (body, routeParams) =>
{
  try
    {
        if (!int.TryParse(routeParams["id"], out int userId))
            return (400, "text/plain", "Invalid user id");

        var parsed = JsonSerializer.Deserialize<RegisterRequest>(body, 
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (parsed is null) return (400, "text/plain", "Invalid JSON body");

        var user = await UserRepository.UpdateUser(userId, parsed.Name, parsed.Email);

        return (200, "application/json", JsonSerializer.Serialize(new { id = user }));
    
    } catch (JsonException) { return (400, "text/plain", "Invalid JSON body"); }
};

routes[("DELETE", "/users/{id}")] = async (body, routeParams) =>
{
    try
    {
        if (!int.TryParse(routeParams["id"], out int userId))
        return (400, "text/plain", "Invalid user id");

        var user = await UserRepository.DeleteUser(userId);

        return (200, "application/json", JsonSerializer.Serialize(new { message = $"User ID : {user} Successfully removed!" }));
    }
    catch (Exception ex) when (ex.Message == "User not found") { return (404, "text/plain", "User not found"); }
};

// Server

var listener = new TcpListener(IPAddress.Any, 8080);
listener.Start();
Console.WriteLine("Listening on http://localhost:8080");

while (true)
{
    TcpClient client = await listener.AcceptTcpClientAsync();
    _ = Task.Run(() => HandleClientAsync(client));
}

async Task HandleClientAsync(TcpClient client)
{
    try
    {
        using var stream = client.GetStream(); // Raw binary
        using var reader = new StreamReader(stream); // Convert to text
        using var writer = new StreamWriter(stream) { AutoFlush = true };

        string? requestLine = await reader.ReadLineAsync(); // Reads until \r\n
        if (requestLine is null) return;

        string[] parts = requestLine.Split(' '); // Splits it at whitespace
        string method = parts[0];
        string path = parts[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue; // Broken header, skip

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim(); // + 1 is to remove the colon
            headers[key] = value;
        }

        string rawBody = "";
        if (headers.TryGetValue("Content-Length", out string? lengthStr) // key : value > lengthstr > int contentLength
            &&  int.TryParse(lengthStr, out int contentLength)
            && contentLength > 0)
        {
            char[] buffer = new char[contentLength];
            await reader.ReadBlockAsync(buffer, 0, contentLength);
            rawBody = new string(buffer);
        }

        // pipeline: ErrorMiddleware → LoggingMiddleware → RunHandler
        var (statusCode, contentType, responseBody) = await ErrorMiddleware(method, path, rawBody, () =>
            LoggingMiddleware(method, path, rawBody, () =>
                RunHandler(method, path, rawBody)));


        await writer.WriteAsync(
            $"HTTP/1.1 {statusCode} OK\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {responseBody.Length}\r\n" +
            $"\r\n" +
            responseBody);
    }
    catch (Exception err) { Console.WriteLine($"Error {err.Message}"); }
    finally { client.Close(); }
}

// Route helper
bool TryMatchRoute(string pattern, string path, out Dictionary<string, string> routeParams)
{
    routeParams = new Dictionary<string, string>();

    string[] patternSegments = pattern.Split('/');
    string[] pathSegments = path.Split('/');

    if (patternSegments.Length != pathSegments.Length)
        return false;

    for (int i = 0; i < patternSegments.Length; i++)
    {
        string patternSeg = patternSegments[i];
        string pathSeg = pathSegments[i];

        if (patternSeg.StartsWith('{') && patternSeg.EndsWith('}'))
        {
            // Extract the value > id : value
            string paramName = patternSeg[1..^1]; // Strips { }, from index 1 to 1 from the end
            routeParams[paramName] = pathSeg; // paramName = key(stripped) : pathSeg = value
        }
        else if (patternSeg != pathSeg)
        {

            return false;
        }
    }

    return true;
}

// Middleware
async Task<(int, string, string)> LoggingMiddleware(
    string method,
    string path,
    string body,
    Func<Task<(int, string, string)>> next)
{
    Console.WriteLine($"→ {method} {path}");
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    var (status, contentType, responseBody) = await next(); // Run the handler > awaits result

    stopwatch.Stop();
    Console.WriteLine($"← {status} ({stopwatch.ElapsedMilliseconds}ms)");

    return (status, contentType, responseBody);
}

async Task<(int, string, string)> ErrorMiddleware(
    string method,
    string path,
    string body,
    Func<Task<(int, string, string)>> next)
{
    try
    {
        return await next(); // Wraps request in try/catch
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Unhandled error on {method} {path}: {ex.Message}");
        return (500, "application/json", JsonSerializer.Serialize(new { error = "Internal server error" }));
    }
}

async Task<(int, string, string)> RunHandler(string method, string path, string body)
{
    var match = routes
    .Where(r => r.Key.Method == method) // ex, if POST > discard all GET and so on
    .OrderBy(r => r.Key.Path.Contains('{') ? 1 : 0)
    .Select(r => (Route: r, Matched: TryMatchRoute(r.Key.Path, path, out var p), Params: p)) // 
    .FirstOrDefault(r => r.Matched);
    //Console.WriteLine($"Matched: {match.Matched}, Params: {string.Join(", ", match.Params.Select(p => $"{p.Key}={p.Value}"))}"); // foreach var p in match.Params

    if (match.Route.Value is not null)
    {
        var (status, contentType, responseBody) = await match.Route.Value(body, match.Params);
        return (status, contentType, responseBody);
    }
    return (404, "text/plain", "Not found");
}

// Data
record EchoRequest(string Message);
record RegisterRequest(string Name, string Email);

public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
};

// DB interaction with users table
public class UserRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public UserRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<User?> GetUserById(int id)
    {
        await using var cmd = _dataSource.CreateCommand(
            "SELECT id, name, email FROM users WHERE id = @id");
        
        cmd.Parameters.AddWithValue("id", id); // To prevent SQL-Injection

        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync()) return null;

        return new User
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            Email = reader.GetString(reader.GetOrdinal("email"))
        };
    }

    public async Task<List<User>> GetAllUsers()
    {
        var users = new List<User>();

        await using var cmd = _dataSource.CreateCommand("SELECT * FROM users");

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var user = new User
            {
                Id = reader.GetInt32(reader.GetOrdinal("id")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                Email = reader.GetString(reader.GetOrdinal("email"))
            };

            users.Add(user);
        }

        return users;
    }

    public async Task<int> CreateUser(string name, string email)
    {
        await using var cmd = _dataSource.CreateCommand(
            "INSERT INTO users (name, email) VALUES (@name, @email) RETURNING id");
        
        cmd.Parameters.AddWithValue("@name",  name); // To prevent SQL-Injection
        cmd.Parameters.AddWithValue("@email", email); 

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new Exception("Insert failed, no row returned");

        return reader.GetInt32(0); // ID
    }

    public async Task<int> UpdateUser(int id, string name, string email)
    {
        await using var cmd = _dataSource.CreateCommand(
            "UPDATE users SET name = @name, email = @email WHERE id = @id RETURNING id");
        
        cmd.Parameters.AddWithValue("@name",  name);
        cmd.Parameters.AddWithValue("@email", email); 
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new Exception("User not found");

        return reader.GetInt32(0); // ID
    }

    public async Task<int> DeleteUser(int id)
    {
        await using var cmd = _dataSource.CreateCommand(
            "DELETE FROM users WHERE id = @id RETURNING id");
        
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new Exception("User not found");

        return reader.GetInt32(0);
    }
}