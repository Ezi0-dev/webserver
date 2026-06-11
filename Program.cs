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

        if (method == "POST" && path == "/echo")
        {
            EchoRequest? parsed = null;
            try { parsed = JsonSerializer.Deserialize<EchoRequest>(rawBody,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (JsonException jsonerror) { Console.WriteLine($"JSON error : {jsonerror}"); }

            if (parsed is null)
            {
                statusCode = 400; contentType = "text/plain";
                responseBody = "Invalid JSON body";
            }
            else
            {
                statusCode = 200; contentType = "application/json";
                responseBody = JsonSerializer.Serialize(new { recieved = parsed.Message });
            }
        }
        else if (method == "GET" && path == "/hello")
        {
            statusCode = 200; contentType = "text/plain";
            responseBody = "Hello, world!";
        }
        else
        {
            statusCode = 404; contentType = "text/plain";
            responseBody = "Not found : (";
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