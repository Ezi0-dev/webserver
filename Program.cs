using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

var listener = new TcpListener(IPAddress.Any, 8080);
listener.Start();

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


        string responseBody;
        int statusCode;
        string contentType;

        // key : value
        var routes = new Dictionary<(string Method, string Path), Func<string, Dictionary<string, string>, Task<(int Status, string ContentType, string Body)>>>();

        routes[("GET", "/hello")] = async (body, routeParams) => { return (200, "text/plain", "Hello, bro"); };

        routes[("GET", "/users/{id}")] = async (body, routeParams) =>
        {
            return (200, "text/plain", $"You asked for user {routeParams["id"]}");
        };

        routes[("POST", "/echo")] = async (body, routeParams) =>
        {
            try
            {
                Console.WriteLine(body);
                var parsed = JsonSerializer.Deserialize<EchoRequest>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (parsed is null)
                    return (400, "text/plain", "Invalid JSON body");

                string json = JsonSerializer.Serialize(new { received = parsed.Message });
                return (200, "application/json", json);
            }
            catch (JsonException)
            {
                return (400, "text/plain", "Invalid JSON body");
            }
        };

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
record EchoRequest(string Message);