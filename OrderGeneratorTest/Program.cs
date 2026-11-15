using System.Buffers;
using System.IO.Pipes;
using LimitOrderBook.Models;

namespace LimitOrderBookTester;

class Program
{
    // Order Book Generator Simulation
    
    static async Task Main(string[] args)
    {
        await RunGenerator(1000);
    }

    static async ValueTask RunGenerator(int numberOfOrders)
    {
        Console.WriteLine($"Running {numberOfOrders} orders...");
        Console.WriteLine();
        
        List<int> orderNumbers = new List<int>();
        
        const double highPrice = 12.00;
        const double lowPrice = 11.75;
        const int lowQuantity = 1;
        const int highQuantity = 50;
        
        OrderSide side = OrderSide.Buy;
        OrderType orderType = OrderType.Limit;
        char action = 'A';
        int newOrderId = 0;

        await using var pipeClient = new NamedPipeClientStream(
            ".",
            "LOBServerStream",
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        
        await pipeClient.ConnectAsync(5000);
        Console.WriteLine("Connected to LOB Pipe Server Listener.");
        
        await using var writer = new StreamWriter(pipeClient) { AutoFlush = true };
        
        for (int index = 0; index < numberOfOrders; index++)
        {
            if (Random.Shared.Next() % 10 > 5)
                side = OrderSide.Buy;
            else 
                side = OrderSide.Sell;
            
            double price = ((highPrice - lowPrice) * Random.Shared.NextDouble()) + lowPrice;
            int quantity = Random.Shared.Next(lowQuantity, highQuantity);

            if (Random.Shared.Next() % 10 > 6)
                orderType = OrderType.Limit;
            else
                orderType = OrderType.Market;

            if (Random.Shared.Next() % 3 > 0 || !orderNumbers.Any())
            {
                action = 'A';
                do 
                {
                    newOrderId = Random.Shared.Next();
                }
                while (orderNumbers.IndexOf(newOrderId) != -1);
                                    
                orderNumbers.Add(Random.Shared.Next());
            }
            else
            {
                action = 'C';
                newOrderId = orderNumbers.First();
                
                orderNumbers.Remove(orderNumbers.First());
            }

            await SubmitOrder(newOrderId, side, price, quantity, action, orderType, writer);
            
            Console.WriteLine($" * [{index}] Sent Order #{newOrderId}: {action} ({orderType}) --> {side} : ${Math.Round(price * 100.0) / 100.0} : {quantity}");
        }
        
        Console.WriteLine();
        Console.WriteLine("Finished generating all orders.");
    }

    static async ValueTask SubmitOrder(int orderId, OrderSide side, double price, int quantity, char action, 
        OrderType orderType, StreamWriter streamWriter)
    {
        // The format of the char buffer data is:
        //  <ORDER-ID>|<REQ-TYPE>|<ORDER-TYPE>|<PRICE>|<SIDE>|<QTY>|<ACCT-ID>|<SECURITY-ID>|<SENT-TIME>
        //  1234567890123|A|L|1235000|B|200|98765432109|1384|4652763476576474

        char typeS = orderType == OrderType.Limit ? 'L' : 'M';
        char sideS = side == OrderSide.Sell ? 'S' : 'B';
        long timestamp = DateTime.Now.Ticks;
        long priceMant = (long)(price * 10000);

        int bytes = 0;
        char[] buffer = ArrayPool<char>.Shared.Rent(512);
        
        buffer.TryWrite($"{orderId}|{action}|{typeS}|{priceMant}|{sideS}|{quantity}|12345678|99999|{timestamp}",
            out bytes);
        
        // Convert to the text stream format and submit to the pipe server
        await streamWriter.WriteLineAsync(buffer.AsMemory(0, bytes));
        
        ArrayPool<char>.Shared.Return(buffer, true);
    }
}