using System.Net;
using System.Net.Sockets;

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
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true };

        string? requestLine = await reader.ReadLineAsync();
        if (requestLine is null) return;

        string[] parts = requestLine.Split(' ');
        string method = parts[0];
        string path = parts[1];

        while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }

        string body;
        int status;
        if (path == "/hello") { status = 200; body = "Hello Dude"; }
        else if (path == "/time") {status = 200; body = DateTime.Now.ToString(); }
        else { status = 404; body = "Not found : ("; }

        await writer.WriteAsync(
            $"HTTP/1.1 {status} OK\r\n" +
            $"Content-Type: text/plain\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            $"\r\n" +
            body);
    }
    catch (Exception err)
    {
        Console.WriteLine($"Error {err.Message}");
    }
    finally
    {
        client.Close();
    }
}