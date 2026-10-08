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
    private OrderBookEngine _Engine = new OrderBookEngine(1024, 256);
     
    static async Task Main(string[] args)
    {
        var cts = new CancellationTokenSource();
        var instance = new Program();
        Console.WriteLine("Limit Order Book Engine - Running...");
        Console.WriteLine("Press Ctrl+C to exit...");
        Console.WriteLine("====================================");

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
            Console.WriteLine("====================================");
            Console.WriteLine("Finished processing.");
        }
    }

    public Program()
    {
        var orderPool = ArrayPool<Order>.Shared.Rent(128);
        ArrayPool<Order>.Shared.Return(orderPool, true);

        var charPool = ArrayPool<char>.Shared.Rent(1024);
        ArrayPool<char>.Shared.Return(charPool, true);
    }

    public async Task Run(CancellationToken ct)
    {
        ReadResult readResult = default;
        byte[] utf8Buffer = new byte[100]; 
        Order[] newOrders = ArrayPool<Order>.Shared.Rent(1);
        Order newOrder = newOrders[0];

        const byte separator = (byte)'|';
        int lastPosition = 0;
        int splitPosition = 0;
        long length = 0;
        int fieldNumber = 0;
        int tempInt = 0;
        long tempLong = 0;
        char tempChar = '\0';
        char requestType = '\0';
        char[] charBuffer = null;

        try
        {
            // Set up a listener from another IPC channel
            using var pipeServer = new NamedPipeServerStream(
                "LOBServerStream", 
                PipeDirection.InOut, 
                1, 
                PipeTransmissionMode.Byte, 
                PipeOptions.Asynchronous);

            while (!ct.IsCancellationRequested)
            {
                await pipeServer.WaitForConnectionAsync(ct);
                // Create an order from the listener input data buffer

                var reader = PipeReader.Create(pipeServer, new StreamPipeReaderOptions(leaveOpen: true, bufferSize: 256));

                while (!ct.IsCancellationRequested && pipeServer.IsConnected)
                {
                    readResult = await reader.ReadAsync(ct);
                    ReadOnlySequence<byte> buffer = readResult.Buffer;
                    fieldNumber = 0;
                    lastPosition = 0;
                    length = buffer.Length;
                    
                    // Convert the char array to an Order object
                    // The format of the char buffer data is:
                    //  <ORDER-ID>|<REQ-TYPE>|<ORDER-TYPE>|<PRICE>|<SIDE>|<QTY>|<FIRM-ID>|<ACCT-ID>|<SECURITY-ID>|<SENT-TIME>

                    // Detail descriptions:
                    //  <REQ-TYPE> is "A" for add new order or "C" for cancel existing order
                    //  <ORDER-TYPE> is "M" (market order) or "L" (limit order) - <PRICE> is ignored for "M"
                    //  <PRICE> is mantissa format (real price rounded to four decimals * 10000) long int
                    //  <SIDE> is "B" (buy) or "S" (sell)
                    //  <SENT-TIME> is unix timestamp
                    // All -ID fields are integers (not alphanumeric)

                    // Examples:
                    //  1234567890123|A|L|1235000|B|200|99887766|98765432109|1384|4652763476576474
                    //  2345678901234|C|L|140000|S|50|99887766|98765432109|417|4673899928307501
                    //  3456789012345|A|B|0|M|100|99887766|98765432109|1638|4678277000145185
 
                    if (buffer.Length > 0)
                    {
                        requestType = '\0';
                        charBuffer = ArrayPool<char>.Shared.Rent((int)buffer.Length);
                        charBuffer.AsSpan().Clear();
                        ReadOnlySpan<byte> bufferSpan = buffer.FirstSpan;

                        for (int index = 0; index < buffer.Length; index++)
                            charBuffer[index] = (char)bufferSpan[index];
                        
                        Console.Write("-- Received data:  ");
                        Console.WriteLine(charBuffer.AsSpan());
                        
                        while (lastPosition < length && fieldNumber < 10)
                        {
                            ReadOnlySequence<byte> seqSlice = buffer.Slice(lastPosition, length);
                            SequencePosition? seqPosition = seqSlice.PositionOf(separator);

                            splitPosition = seqPosition.HasValue ? seqPosition.Value.GetInteger() : (int)length;

                            ReadOnlySequence<byte> seqFieldData = buffer.Slice(lastPosition, splitPosition);
                            utf8Buffer.AsSpan().Clear();
                            seqFieldData.CopyTo(utf8Buffer.AsSpan());

                            switch (fieldNumber)
                            {
                                case 0:
                                    // Order-ID (long)
                                    if (!Int64.TryParse(utf8Buffer, out tempLong))
                                        throw new FormatException("Order-ID must be 64-bit integer");
                                    newOrder.OrderId = tempLong;
                                    break;
                                
                                case 1:
                                    // Request-Type (char)
                                    tempChar = (char)utf8Buffer[0];
                                    if (tempChar == 'A' || tempChar == 'C')
                                        requestType = tempChar;
                                    else
                                        throw new FormatException("Req-Type must be 'A' or 'C'");
                                    break;
                                        
                                case 2:
                                    // Order-Type (char)
                                    tempChar = (char)utf8Buffer[0];
                                    if (tempChar == 'L')
                                        newOrder.OrderType = OrderType.Limit;
                                    else if (tempChar == 'M')
                                        newOrder.OrderType = OrderType.Market;
                                    else
                                        throw new FormatException("Order-Type must be 'L' or 'M'");
                                    break;
                                
                                case 3:
                                    // Price (long)
                                    if (!Int64.TryParse(utf8Buffer, out tempLong))
                                        throw new FormatException("Price must be 64-bit integer");
                                    newOrder.Price = tempLong;
                                    break;

                                case 4:
                                    // Side (char)
                                    tempChar = (char)utf8Buffer[0];
                                    if (tempChar == 'B')
                                        newOrder.Side = OrderSide.Buy;
                                    else if (tempChar == 'S')
                                        newOrder.Side = OrderSide.Sell;
                                    else
                                        throw new FormatException("Side must be 'B' or 'S'");
                                    break;

                                case 5:
                                    // Qty (int)
                                    if (!Int32.TryParse(utf8Buffer, out tempInt))
                                        throw new FormatException("Quantity must be 32-bit integer");
                                    newOrder.TotalQuantity = tempInt;
                                    break;

                                case 6:
                                    // Firm-ID (int)
                                    if (!Int32.TryParse(utf8Buffer, out tempInt))
                                        throw new FormatException("Firm-ID must be 32-bit integer");
                                    newOrder.FirmId = tempInt;
                                    break;

                                case 7:
                                    // Acct-ID (long)
                                    if (!Int64.TryParse(utf8Buffer, out tempLong))
                                        throw new FormatException("Account-ID must be 64-bit integer");
                                    newOrder.AccountId = tempLong;
                                    break;

                                case 8:
                                    // Security-ID (long)
                                    if (!Int64.TryParse(utf8Buffer, out tempLong))
                                        throw new FormatException("Security-ID must be 64-bit integer");
                                    newOrder.SecurityId = tempLong;
                                    break;

                                case 9:
                                    // Sent-Time (long)
                                    if (!Int64.TryParse(utf8Buffer, out tempLong))
                                        throw new FormatException("Sent-Time must be 64-bit integer");
                                    newOrder.Timestamp = tempLong;
                                    break;
                            }
                            
                            lastPosition = splitPosition + 1;
                            length = buffer.Length - lastPosition;
                            fieldNumber++;
                        }

                        OrderStatus status = OrderStatus.Unknown;
                        
                        // Determine the order type and call the appropriate method to handle
                        switch (requestType)
                        {
                            case 'A':
                                status = _Engine.AddOrder(ref newOrder);
                                break;
                            
                            case 'C':
                                status = _Engine.CancelOrder(ref newOrder);
                                break;
                            
                            default:
                                Console.WriteLine("Invalid request type - request is skipped");
                                status = OrderStatus.Rejected;
                                break;
                        }
                        
                        //  * Send notification back to originator
                        _Engine.NotifyOrderFillStatus(ref newOrder, status);
                    }
                    else
                    {
                        Console.WriteLine("-- Empty data received.");
                    }
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
            ArrayPool<Order>.Shared.Return(newOrders);
            
            if (charBuffer is not null)
                ArrayPool<char>.Shared.Return(charBuffer);
        }
    }
    
    public void Dispose()
    {
        _Engine.Dispose();
    }
}