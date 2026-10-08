using System.Buffers;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Text;
using LimitOrderBook.Models;
using LimitOrderBook.Services;
using PipeOptions = System.IO.Pipes.PipeOptions;

namespace LimitOrderBook;

class Program : IDisposable
{
    private OrderBookEngine _engine = new OrderBookEngine(1024, 256);
     
    static async Task Main(string[] args)
    {
        var cts = new CancellationTokenSource();
        var instance = new Program();
        Console.WriteLine("Limit Order Book Engine - Running...");
        Console.WriteLine("Press Ctrl+C to exit...");

        Console.CancelKeyPress += (sender, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await instance.Run(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Finished processing.");
        }
    }

    public Program()
    {
        var orderPool = ArrayPool<Order>.Shared.Rent(128);
        ArrayPool<Order>.Shared.Return(orderPool, true);
    }

    public async Task Run(CancellationToken ct)
    {
        ReadResult readResult;
        Order newOrder = default;
        const byte separator = (byte)'|';

        // Set up a listener from another IPC channel
        using var pipeServer = new NamedPipeServerStream(
            "LOBServerStream", 
            PipeDirection.InOut, 
            1, 
            PipeTransmissionMode.Byte, 
            PipeOptions.Asynchronous);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await pipeServer.WaitForConnectionAsync(ct);
                // Create an order from the listener input data buffer

                var reader = PipeReader.Create(pipeServer, new StreamPipeReaderOptions(leaveOpen: true, bufferSize: 256));

                while (!ct.IsCancellationRequested && pipeServer.IsConnected)
                {
                    readResult = await reader.ReadAsync(ct);
                    ReadOnlySequence<byte> buffer = readResult.Buffer;
                    
                    if (buffer.Length > 0)
                    {
                        var position = buffer.PositionOf(separator);
                        
                        // Convert the char array to an Order object
                        // The format of the char buffer data is:
                        //  <ORDER-ID>|<TYPE>|<PRICE>|<SIDE>|<QTY>|<FIRM-ID>|<ACCT-ID>|<SECURITY-ID>|<SENT-TIME>

                        // Detail descriptions:
                        //  <TYPE> is "M" (market order) or "L" (limit order) - <PRICE> is ignored for "M"
                        //  <PRICE> is mantissa format (real price rounded to four decimals * 10000) long int
                        //  <SIDE> is "B" (buy) or "S" (sell)
                        //  <SENT-TIME> is unix timestamp
                        // All -ID fields are integers (not alphanumeric)

                        // Examples:
                        //  1234567890123|L|1235000|B|200|99887766|98765432109|1384|4652763476576474
                        //  2345678901234|L|140000|S|50|99887766|98765432109|417|4673899928307501
                        //  3456789012345|B|0|M|100|99887766|98765432109|1638|4678277000145185

                        //  * Determine the order type and call the appropriate method to handle
                        //  * Send notification back to originator
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
            finally
            {
                //if (newOrder != null)
                //{
                    
                //}
            }
        }
    }
    
    public void Dispose()
    {
        _engine.Dispose();
    }
}