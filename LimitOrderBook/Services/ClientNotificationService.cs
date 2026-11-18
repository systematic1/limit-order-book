using System.Buffers;
using System.IO.Pipes;
using LimitOrderBook.Models;

namespace LimitOrderBook.Services;

public class ClientNotificationService
{
    public bool Enabled { get; set; } = true;
    
    public ClientNotificationService()
    {
    }

    public async ValueTask SendNotification(int accountId, long orderId, OrderStatus status, ReadOnlyMemory<char> message)
    {
        if (Enabled)
        {
            long timestamp = DateTime.Now.Ticks;

            // Submits a message to the NamedPipe stream (to client)
            // <ACCT-ID>|<ORDER-ID>|<STATUS>|<TIMESTAMP>|<DETAIL-MESSAGE>
            // 99999|12345678|4|1236417823646|Filled Buy order with 400 at $14.55

            await using var pipeClient = new NamedPipeClientStream(
                ".",
                "LOBClientNotification",
                PipeDirection.Out,
                PipeOptions.Asynchronous);

            await pipeClient.ConnectAsync(1000);
            Console.WriteLine(" - Connected to Client Response Listener.");

            await using var streamWriter = new StreamWriter(pipeClient) { AutoFlush = true };

            int bytes = 0;
            char[] buffer = ArrayPool<char>.Shared.Rent(512);

            buffer.TryWrite($"{accountId}|{orderId}|{(int)status}|{timestamp}|{message}", out bytes);

            // Convert to the text stream format and submit to the pipe server
            await streamWriter.WriteLineAsync(buffer.AsMemory(0, bytes));

            ArrayPool<char>.Shared.Return(buffer, true);
        }
        else
        {
            Console.WriteLine($"  ** NOTIFY ----> AC={accountId}, ID={orderId}, ST={status}: {message}");
        }
    }
}