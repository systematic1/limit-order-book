using LimitOrderBook.Models;
using LimitOrderBook.Services;

namespace LimitOrderBook;

class Program : IDisposable
{
    private OrderBookEngine _Engine = new OrderBookEngine(1024, 256);
    private Order[] _NewOrders;
    private const int _MaxOrders = 512;

    static async Task Main(string[] args)
    {
        var cts = new CancellationTokenSource();
        var instance = new Program();
        Console.WriteLine("Limit Order Book Engine - Running...");
        Console.WriteLine();
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

    private Program()
    {
        _NewOrders = new Order[_MaxOrders];
        for (int index = 0; index < _NewOrders.Length; index++)
            _NewOrders[index].Clear();
    }

    private async Task Run(CancellationToken ct)
    {
        int orderCount = 0, cancelCount = 0;
        int orderIndexToDelete;
        // Generate some random data to build orders with

        while (orderCount < _MaxOrders && !ct.IsCancellationRequested)
        {
            if (orderCount > 20 && Random.Shared.Next() > 0x731A13EF)
            {
                do
                {
                    orderIndexToDelete = Random.Shared.Next();
                } while (_NewOrders[orderIndexToDelete].OrderId == 0);

                Console.Write("<< CancelOrder ");
                Console.Write(_NewOrders[orderIndexToDelete].OrderId);

                OrderStatus status = _Engine.CancelOrder(ref _NewOrders[orderIndexToDelete]);
                if (status == OrderStatus.Canceled)
                {
                    _NewOrders[orderIndexToDelete].OrderId = 0;
                    cancelCount++;
                }

                Console.Write(" ==> ");
                Console.WriteLine((int)status);
            }
            else
            {
                long orderId = Random.Shared.NextInt64();
                long price = (Random.Shared.NextInt64() % 50) * 10000;
                OrderSide side = (Random.Shared.Next() & 2) == 0 ? OrderSide.Buy : OrderSide.Sell;
                OrderType orderType = (Random.Shared.Next() & 2) == 0 ? OrderType.Limit : OrderType.Market;
                int qty = Random.Shared.Next() % 10000;
                long acctId = Random.Shared.NextInt64();
                int secId = Random.Shared.Next();
                
                _NewOrders[orderCount].Init(orderId, price, side, orderType, qty, qty,
                    0, DateTime.Now.Ticks, TimeToLive.GoodTilCanceled, acctId, secId);

                OrderStatus status = _Engine.AddOrder(ref _NewOrders[orderCount]);
                orderCount++;
                
                Console.Write("AddOrder >> ");
                Console.Write(orderId);
                Console.Write(": $");
                Console.Write(price);
                Console.Write(" ==> ");
                Console.WriteLine((int)status);
            }
        }
        
        Console.WriteLine();
        Console.Write(cancelCount);
        Console.WriteLine(" orders were cancelled");
    }
   
    public void Dispose()
    {
        _Engine.Dispose();
    }
}