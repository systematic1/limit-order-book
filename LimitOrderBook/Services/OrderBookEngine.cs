using System.Buffers;
using LimitOrderBook.Models;

namespace LimitOrderBook.Services;

public class OrderBookEngine
{
    private int Capacity;
    
    public OrderBookEngine(int capacityPowerOf2)
    {
        if ((capacityPowerOf2 & (capacityPowerOf2 - 1)) != 0) 
            throw new ArgumentOutOfRangeException($"The {nameof(capacityPowerOf2)} parameter must be a power of 2.");
        
        Capacity = capacityPowerOf2;
        var orderPool = ArrayPool<Order>.Shared.Rent(Capacity);
        ArrayPool<Order>.Shared.Return(orderPool, true);
    }
}