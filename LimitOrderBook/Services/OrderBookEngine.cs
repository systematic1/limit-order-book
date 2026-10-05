using System.Buffers;
using System.Runtime.CompilerServices;
using LimitOrderBook.Models;

namespace LimitOrderBook.Services;

public class OrderBookEngine : IDisposable
{
    private int _OrderCapacity;
    private int _PriceLevelCapacity;

    private Order[] _Orders;
    private PriceLevel[] _AskPriceLevels;
    private PriceLevel[] _BidPriceLevels;

    private int _OrderHeadIndex = 0;
    private int _OrderTailIndex = 0;
    private int _AskLevelHeadIndex = 0;
    private int _AskLevelTailIndex = 0;
    private int _BidLevelHeadIndex = 0;
    private int _BidLevelTailIndex = 0;
    
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

        // Add the order to OrderBook and find the associated PriceLevel index
        PriceLevel[] priceLevels = (order.Side == OrderSide.Buy) ? _BidPriceLevels : _AskPriceLevels;
        PriceLevel priceLevel;
        
        if (order.PriceLevelIndex != -1)
        {
            if (order.PriceLevelIndex >= priceLevels.Length)
                return OrderStatus.RejectedGeneral;
            
            priceLevel = priceLevels[order.PriceLevelIndex];
        }
        else
        {
            // Find previous price level and update link pointers to insert new price level between
            int priceLevelIndex = FindPriceLevelIndex(order.Price, priceLevels);
            if (priceLevelIndex != -1)
                priceLevelIndex = AddPriceLevel(order, priceLevels);

            priceLevel = priceLevels[priceLevelIndex];
            order.PriceLevelIndex = priceLevelIndex;
        }

        priceLevel.OrderCount++;
        priceLevel.QuantityAvailable += order.TotalQuantity;

        int tailIndex = GetOrderTailIndex();
        if (GetOrderHeadIndex() != tailIndex + 1)
        {
            _Orders[tailIndex] = order;
            _OrderHeadIndex++;
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
        // Validate - Basic
        if (order.OrderType == OrderType.Unknown)
            return OrderStatus.RejectedGeneral;

        if (order.FirmId <= 0)
            return OrderStatus.RejectedGeneral;

        if (order.RemainingQuantity < 0)
            return OrderStatus.RejectedGeneral;

        if (order.Side == OrderSide.Unknown)
            return OrderStatus.RejectedGeneral;

        if (order.TotalQuantity < order.RemainingQuantity)
            return OrderStatus.RejectedGeneral;

        if (order.TotalQuantity == 0)
            return OrderStatus.RejectedGeneral;
        
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindPriceLevelIndex(long price, PriceLevel[] priceLevels)
    {
        for (int i = 0; i < _PriceLevelCapacity; i++)
        {
            if (priceLevels[i].Price == price)
                return i;
        }

        return -1;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetOrderTailIndex() => _OrderHeadIndex & (_OrderCapacity - 1);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetOrderHeadIndex() => _OrderHeadIndex & (_OrderCapacity - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetPriceLevelTailIndex(OrderSide side)
    {
        if (side == OrderSide.Buy)
        {
            _BidPriceLevels;
        }
        else
        {
            _AskPriceLevels;
        }
        //return index & (_PriceLevelCapacity - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetPriceLevelHeadIndex(OrderSide side)
    {
        PriceLevel[] priceLevels = (side == OrderSide.Buy ? _BidPriceLevels : _AskPriceLevels);
        int index = (side == OrderSide.Buy ? _BidLevelHeadIndex : _AskLevelHeadIndex);
        return index & (_PriceLevelCapacity - 1);
    }

    private int AddPriceLevel(ref readonly Order order, ref PriceLevel[] priceLevels)
    {
        // Find the price level with a price just above or below depending on OrderSide
        long nextLowestPrice = 0L;
        int nextLowestPriceIndex = -1;
        int availableNewPriceIndex = -1;
        
        for (int i = 0; i < priceLevels.Length; i++)
        {
            if (priceLevels[i].Price < order.Price && priceLevels[i].Price > nextLowestPrice)
            {
                nextLowestPrice = priceLevels[i].Price;
                nextLowestPriceIndex = i;
            }
            else
                break;
        }

        int tailIndex = GetPriceLevelTailIndex(order.Side);
        if (GetPriceLevelHeadIndex(order.Side) != tailIndex + 1)
        {
            availableNewPriceIndex = tailIndex;
            
            int nextHigherPriceIndex = priceLevels[nextLowestPriceIndex].NextIndex;
            priceLevels[nextLowestPrice].NextIndex = availableNewPriceIndex;
            priceLevels[nextHigherPriceIndex].PrevIndex = availableNewPriceIndex;
        }
    }
    
    public void Dispose()
    {
        ArrayPool<PriceLevel>.Shared.Return(_BidPriceLevels);
        ArrayPool<PriceLevel>.Shared.Return(_AskPriceLevels);
        ArrayPool<Order>.Shared.Return(_Orders);
    }
}