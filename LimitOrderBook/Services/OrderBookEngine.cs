using System.Buffers;
using System.Runtime.CompilerServices;
using LimitOrderBook.Models;

namespace LimitOrderBook.Services;

public class OrderBookEngine : IDisposable
{
    private int _OrderCapacity;
    private int _PriceLevelCapacity;
    private int _HeadIndex = 0;
    private int _TailIndex = 0;

    private Order[] _Orders;
    private PriceLevel[] _AskPriceLevels;
    private PriceLevel[] _BidPriceLevels;

    private int _OrderHeadIndex = 0;
    private int _OrderTailIndex = 0;
    
    public OrderBookEngine(int orderCapacityPowerOf2, int priceLevelCapacityPowerOf2)
    {
        if ((orderCapacityPowerOf2 & (orderCapacityPowerOf2 - 1)) != 0) 
            throw new ArgumentOutOfRangeException($"The {nameof(orderCapacityPowerOf2)} parameter must be a power of 2.");
        if ((priceLevelCapacityPowerOf2 & (priceLevelCapacityPowerOf2 - 1)) != 0) 
            throw new ArgumentOutOfRangeException($"The {nameof(priceLevelCapacityPowerOf2)} parameter must be a power of 2.");
        
        _OrderCapacity = orderCapacityPowerOf2;
        _PriceLevelCapacity = priceLevelCapacityPowerOf2;
        
        // Pre-allocate the pools and clear all the pool items
        _Orders = ArrayPool<Order>.Shared.Rent(_OrderCapacity);
        _AskPriceLevels = ArrayPool<PriceLevel>.Shared.Rent(_PriceLevelCapacity);
        _BidPriceLevels = ArrayPool<PriceLevel>.Shared.Rent(_PriceLevelCapacity);
    }

    public OrderStatus AddOrder(ref Order order)
    {
        OrderStatus status = ValidateOrder(ref order);
        if (status != OrderStatus.Unknown)
            return status;

        if (DoesOrderViolateRiskCheck(ref order))
            return OrderStatus.RejectedGeneral;
        
        if (IsDuplicateOrder(order.OrderId))
            return OrderStatus.Duplicate;

        int orderIndex = FindOrderIndex(order.OrderId);
        PriceLevel[] priceLevels;
        
        if (_Orders[orderIndex].PriceLevelIndex != -1)
        {
            priceLevels = (order.Side == OrderSide.Buy) ? _BidPriceLevels : _AskPriceLevels;
            
            if (_Orders[orderIndex].PriceLevelIndex >= priceLevels.Length)
                return OrderStatus.RejectedGeneral;
            
            PriceLevel priceLevel = priceLevels[_Orders[orderIndex].PriceLevelIndex];
        }
        else
        {
            // Create new price level and set quantity
            // Find previous price level and update link pointers to insert new price level between
        }
        
        // Get the oldest time-priority order and fill it (should be at head index)
        // If there is enough to fill entire order, update the remaining and return
        // Otherwise do a partial fill with available, then move to the next available
        // price level (direction depends on whether buy or sell order) to fill again (while)
        
        return OrderStatus.Resting;
    }

    public OrderStatus CancelOrder(ref Order order)
    {
        OrderStatus status = ValidateOrder(ref order);
        if (status != OrderStatus.Unknown)
            return status;
        
        int orderIndex = FindOrderIndex(order.OrderId);
        if (orderIndex == -1)
            return OrderStatus.NotFound;

        // Remove the order from the order list and update the corresponding price level
        // remaining quantity and order count accordingly
        
        return OrderStatus.Canceled;
    }

    public OrderStatus ModifyOrder(ref Order order)
    {
        return OrderStatus.RejectedGeneral;
    }

    private OrderStatus ValidateOrder(ref Order order)
    {
        // Validate 
        return OrderStatus.Unknown;
    }

    private bool DoesOrderViolateRiskCheck(ref Order order)
    {
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsDuplicateOrder(long orderId)
    {
        int orderIndex = FindOrderIndex(orderId);
        return (orderIndex != -1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindOrderIndex(long orderId)
    {
        for (int i = 0; i < _OrderCapacity; i++)
        {
            if (_Orders[i].OrderId == orderId)
                return i;
        }

        return -1;
    }
    
    public void Dispose()
    {
        ArrayPool<PriceLevel>.Shared.Return(_BidPriceLevels);
        ArrayPool<PriceLevel>.Shared.Return(_AskPriceLevels);
        ArrayPool<Order>.Shared.Return(_Orders);
    }
}