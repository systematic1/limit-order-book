using System.Buffers;
using System.IO.Pipelines;
using System.IO.Pipes;
using PipeOptions = System.IO.Pipes.PipeOptions;

namespace ClientReceiverTest;

class Program
{
    static void Main(string[] args)
    {
        ActivateListener().GetAwaiter().GetResult();
    }

    static async ValueTask ActivateListener()
    {
        var cts = new CancellationTokenSource();
        var instance = new Program();
        Console.WriteLine("Client OrderBook Receiver - Running...");
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
            await instance.Run(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("=======================================================================");
            Console.WriteLine("Finished processing.");
        }
    }

    public async ValueTask Run(CancellationToken ct)
    {
        const byte messageDelimiter = (byte)'\n';
        int lastOrderIndex = -1;

        while (!ct.IsCancellationRequested)
        {
            // Set up a listener from another IPC channel
            await using var pipeServer = new NamedPipeServerStream(
                "LOBClientNotification",
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
                    Console.WriteLine("Client is receiving data...");

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
                            while (!sequenceReader.End)
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

                                ParseAndReport(messageSequence);
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
        }
    }

    private static void ParseAndReport(ReadOnlySequence<byte> messageSequence)
    {
        Span<byte> utf8Buffer = stackalloc byte[Math.Max(128, (int)messageSequence.Length)];
        Span<char> messageBuffer = stackalloc char[Math.Max(256, (int)messageSequence.Length)];
        
        const byte separator = (byte)'|';
        int fieldNumber = 0;
        int accountId = -1;
        long orderId = -1L;
        byte status = 255;
        long timestamp = -1L;

        // <ACCT-ID>|<ORDER-ID>|<STATUS>|<TIMESTAMP>|<DETAIL-MESSAGE>
        // 99999|12345678|4|1236417823646|Filled Buy order with 400 at $14.55

        SequenceReader<byte> fieldReader = new SequenceReader<byte>(messageSequence);
        while (!fieldReader.End && fieldNumber < 5)
        {
            fieldReader.TryReadTo(out ReadOnlySequence<byte> fieldSeq, separator);
            // If it's the last field, read the remainder
            if (fieldSeq.IsEmpty && fieldNumber == 4)
                fieldSeq = fieldReader.Sequence.Slice(fieldReader.Position);

            utf8Buffer.Clear();
            fieldSeq.CopyTo(utf8Buffer);
            ReadOnlySpan<byte> fieldSpan = utf8Buffer.Slice(0, (int)fieldSeq.Length);

            switch (fieldNumber)
            {
                case 0:
                    // Account-ID (int)
                    if (!Int32.TryParse(fieldSpan, out accountId))
                        throw new FormatException("Account-ID must be 32-bit integer");
                    break;

                case 1:
                    // Order-ID (long)
                    if (!Int64.TryParse(fieldSpan, out orderId))
                        throw new FormatException("Order-ID must be 64-bit integer");
                    break;

                case 2:
                    // Status (byte)
                    if (Byte.TryParse(fieldSpan, out status))
                        throw new FormatException("Status-Code must be a byte-size integer");
                    break;
                
                case 3:
                    // Timestamp (long)
                    if (Int64.TryParse(fieldSpan, out timestamp))
                        throw new FormatException("Timestamp must be a 64-bit integer");
                    break;
                
                case 4:
                    // Message (char*)
                    messageBuffer.Clear();
                    for (int index = 0; index < fieldSpan.Length; index++) // convert byte to chars
                        messageBuffer[index] = (char)fieldSpan[index];
                    break;
                
                default:
                    break;
            }

            fieldNumber++;
        }
        
        Console.WriteLine($"Received -> Order {orderId}, status {status}, timestamp {timestamp}, message: {messageBuffer}");
    }
}