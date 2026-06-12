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
        }

        string responseBody;
        int statusCode;
        string contentType;

        // key : value > 
        var routes = new Dictionary<(string Method, string Path), Func<string, Task<(int Status, string ContentType, string Body)>>>();

        routes[("GET", "/hello")] = async body => { return (200, "text/plain", "Hello, bro"); };

        routes[("POST", "/echo")] = async body =>
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

        if (routes.TryGetValue((method, path), out var handler))
        {
            (statusCode, contentType, responseBody) = await handler(rawBody);
        }
        else
        {
            statusCode = 404;
            contentType = "text/plain";
            responseBody = "Not found";
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