using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text.Json;

// Temp storage
var users = new List<User>();
var nextId = 1;
var usersLock = new object();

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
    List<User> snapshot;
    lock (usersLock)
    {
        snapshot = users.ToList();
    }
    return (200, "application/json", JsonSerializer.Serialize(snapshot));
};

routes[("GET", "/users/{id}")] = async (body, routeParams) =>
{
    if (!int.TryParse(routeParams["id"], out int id))
        return (400, "text/plain", "Invalid user id");

    User? found;
    lock (usersLock)
    {
        found = users.FirstOrDefault(u => u.Id == id);
    }

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

        lock (usersLock)
        {
            if (users.Any(u => u.Email == parsed.Email) || users.Any(u => u.Name == parsed.Name))
                return (400, "text/plain", "Name or Email already in use!");

            users.Add(new User{ Id = nextId, Name = parsed.Name, Email = parsed.Email});
            nextId++;
        }
        return (201, "application/json", $"User {parsed.Name} registered");
        
    } catch (JsonException) { return (400, "text/plain", "Invalid JSON body"); }
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
        //string version = parts[2];

        //parts.ToList().ForEach(i => Console.WriteLine(i.ToString()));
        Console.WriteLine($"Method={method}, Path={path}");

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
            //Console.WriteLine($"rawBody is : {rawBody}");
            Console.WriteLine($"Body (raw): {rawBody}");
            Console.WriteLine($"Body length: {rawBody.Length}, Content-Length header: {headers.GetValueOrDefault("Content-Length")}");
        }

        string responseBody;
        int statusCode;
        string contentType;

        var match = routes
            .Where(r => r.Key.Method == method) // ex, if POST > discard all GET and so on
            .OrderBy(r => r.Key.Path.Contains('{') ? 1 : 0)
            .Select(r => (Route: r, Matched: TryMatchRoute(r.Key.Path, path, out var p), Params: p)) // 
            .FirstOrDefault(r => r.Matched);
            Console.WriteLine($"Matched: {match.Matched}, Params: {string.Join(", ", match.Params.Select(p => $"{p.Key}={p.Value}"))}"); // foreach var p in match.Params

        if (match.Route.Value is not null)
        {
            (statusCode, contentType, responseBody) = await match.Route.Value(rawBody, match.Params);
            Console.WriteLine($"Responding: {statusCode} {contentType} -> {responseBody}");
        }
        else
        {
            (statusCode, contentType, responseBody) = (404, "text/plain", "Not found");
        }

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

// Data
record EchoRequest(string Message);
record RegisterRequest(string Name, string Email);

class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
};