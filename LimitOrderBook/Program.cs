using System.Buffers;
using System.IO.Pipelines;
using System.IO.Pipes;
using LimitOrderBook.Models;
using LimitOrderBook.Services;
using PipeOptions = System.IO.Pipes.PipeOptions;

namespace LimitOrderBook;

class Program : IDisposable
{
    private OrderBookEngine _Engine = new OrderBookEngine(4096, 512);
     
    static async Task Main(string[] args)
    {
        var cts = new CancellationTokenSource();
        var instance = new Program();
        Console.WriteLine("Limit Order Book Processing Engine - Running...");
        Console.WriteLine();
        Console.WriteLine("Press Ctrl+C to exit...");
        Console.WriteLine("===========================================================================");

        Console.CancelKeyPress += (sender, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        try
        {
            //await instance.RunListener(cts.Token);
            instance.RunLocalTest(256);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("=======================================================================");
            Console.WriteLine("Finished processing.");
        }
    }

    public Program()
    {
    }

    public async Task RunListener(CancellationToken ct)
    {
        const byte messageDelimiter = (byte)'\n';
        Order[] newOrders = ArrayPool<Order>.Shared.Rent(1024);
        int lastOrderIndex = -1;

        _Engine.EnableNotifications = true;

        while (!ct.IsCancellationRequested)
        {
            // Set up a listener from another IPC channel
            await using var pipeServer = new NamedPipeServerStream(
                "LOBServerStream",
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipeServer.WaitForConnectionAsync(ct);
                // Create an order from the listener input data buffer

                var reader = PipeReader.Create(pipeServer,
                    new StreamPipeReaderOptions(leaveOpen: true, bufferSize: 256));
                Console.WriteLine("Created PipeServer.");

                while (!ct.IsCancellationRequested && pipeServer.IsConnected)
                {
                    Console.WriteLine("Server is receiving data...");

                    ReadResult readResult = await reader.ReadAsync(ct);
                    ReadOnlySequence<byte> buffer = readResult.Buffer;

                    // If the stream is completed by the client, break out of the loop
                    if (buffer.IsEmpty && readResult.IsCompleted)
                        break;

                    SequencePosition examined = buffer.Start;
                    SequencePosition consumed = buffer.End;

                    if (!buffer.IsEmpty)
                    {
                        try
                        {
                            var sequenceReader = new SequenceReader<byte>(buffer);
                            while (!sequenceReader.End && lastOrderIndex < newOrders.Length)
                            {
                                // Read until the message delimiter (or parse up to length if fixed-size)
                                if (!sequenceReader.TryReadTo(out ReadOnlySequence<byte> messageSequence, messageDelimiter))
                                {
                                    // Incomplete message; wait for more data
                                    examined = buffer.End;
                                    break;
                                }

                                // Mark consumption up to this point
                                consumed = sequenceReader.Position;
                                examined = consumed;

                                ref Order orderItem = ref newOrders[++lastOrderIndex];
                                ProcessAndSend(messageSequence, ref orderItem);
                            }
                        }
                        finally
                        {
                            reader.AdvanceTo(consumed, examined);
                        }
                    }
                    else
                    {
                        Console.WriteLine("-- Empty data received.");
                    }

                    if (readResult.IsCompleted)
                        break;
                }

                if (!pipeServer.IsConnected)
                    pipeServer.Disconnect();
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Cancelled operation.");
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
            finally
            {
                ArrayPool<Order>.Shared.Return(newOrders);
            }
        }
    }
    
    private void ProcessAndSend(ReadOnlySequence<byte> messageSequence, ref Order newOrder)
    {
        Span<byte> utf8Buffer = stackalloc byte[Math.Max(128, (int)messageSequence.Length)];

        const byte separator = (byte)'|';
        int fieldNumber = 0;
        int tempInt = 0;
        long tempLong = 0;
        char tempChar = '\0';
        char requestType = '\0';

        try
        {
            // Convert the char array to an Order object
            // The format of the char buffer data is:
            //  <ORDER-ID>|<REQ-TYPE>|<ORDER-TYPE>|<PRICE>|<SIDE>|<QTY>|<ACCT-ID>|<SECURITY-ID>|<SENT-TIME>

            // Detail descriptions:
            //  <REQ-TYPE> is "A" for add new order or "C" for cancel existing order
            //  <ORDER-TYPE> is "M" (market order) or "L" (limit order) - <PRICE> is ignored for "M"
            //  <PRICE> is mantissa format (real price rounded to four decimals * 10000) long int
            //  <SIDE> is "B" (buy) or "S" (sell)
            //  <SENT-TIME> is unix timestamp
            // All -ID fields are integers (not alphanumeric)

            // Examples:
            //  1234567890123|A|L|1235000|B|200|98765432109|1384|4652763476576474
            //  2345678901234|C|L|140000|S|50|98765432109|417|4673899928307501
            //  3456789012345|A|M|0|B|100|98765432109|1638|4678277000145185

            SequenceReader<byte> fieldReader = new SequenceReader<byte>(messageSequence);
            while (!fieldReader.End && fieldNumber < 9)
            {
                fieldReader.TryReadTo(out ReadOnlySequence<byte> fieldSeq, separator);
                // If it's the last field, read the remainder
                if (fieldSeq.IsEmpty && fieldNumber == 8)
                    fieldSeq = fieldReader.Sequence.Slice(fieldReader.Position);

                utf8Buffer.Clear();
                fieldSeq.CopyTo(utf8Buffer);
                ReadOnlySpan<byte> fieldSpan = utf8Buffer.Slice(0, (int)fieldSeq.Length);

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
                        // Acct-ID (long)
                        if (!Int64.TryParse(utf8Buffer, out tempLong))
                            throw new FormatException("Account-ID must be 64-bit integer");
                        newOrder.AccountId = tempLong;
                        break;

                    case 7:
                        // Security-ID (long)
                        if (!Int64.TryParse(utf8Buffer, out tempLong))
                            throw new FormatException("Security-ID must be 64-bit integer");
                        newOrder.SecurityId = tempLong;
                        break;

                    case 8:
                        // Sent-Time (long)
                        if (!Int64.TryParse(utf8Buffer, out tempLong))
                            throw new FormatException("Sent-Time must be 64-bit integer");
                        newOrder.Timestamp = tempLong;
                        break;
                }

                fieldNumber++;
            }

            OrderStatus status = OrderStatus.Unknown;

            // Determine the order type and call the appropriate method to handle
            switch (requestType)
            {
                case 'A':
                    Console.WriteLine(
                        $"Adding order #{newOrder.OrderId} => {newOrder.Side} {newOrder.Price}, {newOrder.TotalQuantity}, {newOrder.OrderType}");
                    status = _Engine.AddOrder(ref newOrder);
                    break;

                case 'C':
                    Console.WriteLine($"Canceling order #{newOrder.OrderId}");
                    status = _Engine.CancelOrder(ref newOrder);
                    break;

                default:
                    Console.WriteLine("Invalid request type - request is skipped");
                    status = OrderStatus.Rejected;
                    break;
            }

            //  * Send notification back to originator
            _Engine.NotifyOrderFillStatus(newOrder, status, null);
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
            throw;
        }
    }

    public void Dispose()
    {
        _Engine.Dispose();
    }

    public void RunLocalTest(int numberOfOrders)
    {
        Console.WriteLine($"Running {numberOfOrders} orders...");
        Console.WriteLine();

        _Engine.EnableNotifications = false;
        
        List<int> orderNumbers = new List<int>();
        
        const double highPrice = 12.00;
        const double lowPrice = 11.50;
        const int lowQuantity = 1;
        const int highQuantity = 50;
        
        OrderSide side = OrderSide.Buy;
        OrderType orderType = OrderType.Limit;
        char action = 'A';
        int newOrderId = 0;
        
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

            long accountId = ((Random.Shared.Next() % 25L) + 1L) * 11111L; 

            if (Random.Shared.Next() % 3 > 0 || orderNumbers.Count() < 5)
            {
                action = 'A';
                do 
                {
                    newOrderId = Random.Shared.Next();
                }
                while (orderNumbers.IndexOf(newOrderId) != -1);
                                    
                orderNumbers.Add(newOrderId);
            }
            else
            {
                action = 'C';
                newOrderId = orderNumbers.First();
                
                orderNumbers.Remove(orderNumbers.First());
            }

            OrderStatus status = OrderStatus.Unknown;
            var newOrder = new Order()
            {
                OrderId = newOrderId,
                Side = side,
                Price = (long)(price * 10000.0),
                RemainingQuantity = quantity,
                AccountId = accountId,
                OrderType = orderType,
                PriceLevelIndex = -1,
                SecurityId = 88888, 
                Timestamp = DateTime.Now.Ticks, 
                TimeToLive = TimeToLive.GoodTilCanceled, 
                TotalQuantity = quantity
            };
            
            switch (action)
            {
                case 'A':
                    Console.WriteLine(
                        $"Adding order #{newOrder.OrderId} => {newOrder.Side} : ${newOrder.Price} : Q={newOrder.TotalQuantity}, {newOrder.OrderType}, A={newOrder.AccountId}");
                    status = _Engine.AddOrder(ref newOrder);
                    break;

                case 'C':
                    Console.WriteLine($"Canceling order #{newOrder.OrderId}");
                    status = _Engine.CancelOrder(ref newOrder);
                    break;

                default:
                    Console.WriteLine("Invalid request type - request is skipped");
                    status = OrderStatus.Rejected;
                    break;
            }
            
            Console.WriteLine($"   <== {status}");
        }
        
        Console.WriteLine();
        Console.WriteLine("Finished generating all orders.");
        
    }
}