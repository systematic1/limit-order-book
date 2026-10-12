using System.Buffers;
using System.Runtime.CompilerServices;
using LimitOrderBook.Models;

namespace LimitOrderBook.Services;

public class OrderBookEngine : IDisposable
{
    /*

    [√] Make ref struct parameter types readonly [const] by prefixing with "in" or "ref readonly"

    CLAUDE.AI OBSERVATIONS / SUGGESTIONS:

    [√] AddOrder’s while loop can spin forever. FillMatchingOrder does nothing when the best match
        has the same AccountId, and then the loop retries the same level.
    [√] A partial fill sets matchedStatus = Filled. It probably should be PartiallyFilled.
    [√] CancelOrder uses the caller’s order.Price and RemainingQuantity rather than the stored
        order’s values. A stale or incorrect order object would corrupt the level totals.
    [√] In AddOrder, the market-order starting price can be the -1 sentinel for an empty book.

    */

    private OrderCollection _Orders;
    private PriceLevelCollection _AskPriceLevels;
    private PriceLevelCollection _BidPriceLevels;
    
    public OrderBookEngine(int orderCapacity, int priceLevelCapacity)
    {
        _Orders = new OrderCollection(orderCapacity);
        _AskPriceLevels = new PriceLevelCollection(priceLevelCapacity);
        _BidPriceLevels = new PriceLevelCollection(priceLevelCapacity);
    }

    public OrderStatus AddOrder(ref Order order)
    {
        PriceLevel priceLevel;
        int levelIndex;
        int quantityFilled = 1; // Set to 1 so it will enter the while loop the first time
        long price = order.Price;

        OrderStatus status = ValidateOrder(ref order);
        if (status != OrderStatus.Unknown)
            return status;

        if (DoesOrderViolateRisk(ref order))
            return OrderStatus.Rejected;
        
        if (order.Side == OrderSide.Buy)
        {
            // If market order, find the lowest ask price available to start
            if (order.OrderType == OrderType.Market)
            {
                levelIndex = _AskPriceLevels.GetNextHigherPriceLevel(long.MinValue);
                if (levelIndex != -1)
                    price = _AskPriceLevels.GetPriceLevelByIndex(levelIndex).Price;
            }

            // We keep looking for more orders to fill until our order is completed
            while (order.RemainingQuantity > 0 && quantityFilled > 0)
            {
                priceLevel = _AskPriceLevels.GetPriceLevelByPrice(price);
                if (Unsafe.IsNullRef(ref priceLevel))
                {
                    levelIndex = _AskPriceLevels.GetNextLowerPriceLevel(price);
                    if (levelIndex == -1)   // If no more orders are available, jump out of loop here
                        break;

                    priceLevel = _AskPriceLevels.GetPriceLevelByIndex(levelIndex);
                }

                // Find a matching order and fill it
                quantityFilled = FillMatchingOrder(ref priceLevel, ref order);
            }
        }
        else
        {
            // If market order, find the highest bid price available to start
            if (order.OrderType == OrderType.Market)
            {
                levelIndex = _AskPriceLevels.GetNextLowerPriceLevel(long.MaxValue);
                if (levelIndex != -1)
                    price = _AskPriceLevels.GetPriceLevelByIndex(levelIndex).Price;
            }

            // We keep looking for more orders to fill until our order is completed
            while (order.RemainingQuantity > 0 && quantityFilled > 0)
            {
                priceLevel = _BidPriceLevels.GetPriceLevelByPrice(price);
                if (Unsafe.IsNullRef(ref priceLevel))
                {
                    levelIndex = _BidPriceLevels.GetNextHigherPriceLevel(price);
                    if (levelIndex == -1)  // If no more orders are available, jump out of loop here
                        break;

                    priceLevel = _BidPriceLevels.GetPriceLevelByIndex(levelIndex);
                    order.PriceLevelIndex = levelIndex;
                }
                    
                // Find a matching order and fill it
                quantityFilled = FillMatchingOrder(ref priceLevel, ref order);
            }
        }

        order.Timestamp = DateTime.Now.Ticks;

        // If we filled the entire order quantity, then return "filled" status
        if (order.RemainingQuantity == 0)
            return OrderStatus.Filled;

        // If we run out of qualifying limit orders, store the order in the book (resting)
        if (AddOrderToBook(ref order))
            return OrderStatus.PartiallyFilled;
        else
            return OrderStatus.Rejected;
    }

    public OrderStatus CancelOrder(ref Order order)
    {
        OrderStatus status = ValidateOrder(ref order);
        if (status != OrderStatus.Unknown)
            return status;
        
        Order storedOrderRef = _Orders.GetOrder(order.OrderId);
        if (Unsafe.IsNullRef(ref storedOrderRef))
            return OrderStatus.NotFound;

        // Find the price level and adjust the quantity remaining and order count
        PriceLevel priceLevel;
        if (storedOrderRef.Side == OrderSide.Buy)
            priceLevel = _BidPriceLevels.GetPriceLevelByPrice(storedOrderRef.Price);
        else
            priceLevel = _AskPriceLevels.GetPriceLevelByPrice(storedOrderRef.Price);

        if (Unsafe.IsNullRef(ref priceLevel))
            return OrderStatus.NotFound;
            
        priceLevel.QuantityAvailable -= storedOrderRef.RemainingQuantity;
        priceLevel.OrderCount--;
        
        // Remove the order from the order list
        storedOrderRef.PriceLevelIndex = -1;

        if (!_Orders.RemoveOrder(ref storedOrderRef, ref priceLevel))
            return OrderStatus.Rejected;
        
        return OrderStatus.Canceled;
    }

    //public OrderStatus ModifyOrder(ref Order order)
    //{
    //    return OrderStatus.Rejected;
    //}

    public void NotifyOrderFillStatus(ref Order order, OrderStatus status)
    {
        // Not implemented here
    }

    // ======================================================
    
    private OrderStatus ValidateOrder(ref Order order)
    {
        // Validate - Basic
        if (order.RemainingQuantity < 0)
            return OrderStatus.Rejected;

        if (order.TotalQuantity < order.RemainingQuantity)
            return OrderStatus.Rejected;

        if (order.TotalQuantity == 0)
            return OrderStatus.Rejected;
        
        if (IsDuplicateOrder(order.OrderId))
            return OrderStatus.Duplicate;

        return OrderStatus.Unknown;
    }

    private bool DoesOrderViolateRisk(ref Order order)
    {
        // This will not be implemented - always assume risk check passes
        return false;
    }

    private int FillMatchingOrder(ref PriceLevel priceLevel, ref Order order)
    {
        Order matchedOrder;
        OrderSide matchSide = (order.Side == OrderSide.Sell ? OrderSide.Buy : OrderSide.Sell);
        OrderStatus matchedStatus = default;
        PriceLevelCollection priceLevels = (order.Side == OrderSide.Sell ? _BidPriceLevels : _AskPriceLevels);
        int quantityRemaining = order.RemainingQuantity;
        int quantityFilled = 0;

        // Search the order list for the best available order by time-priority
        matchedOrder = _Orders.FindBestAtPriceLevel(ref priceLevel, ref priceLevels, matchSide);
        if (!Unsafe.IsNullRef(ref matchedOrder))
        {
            if (matchedOrder.AccountId != order.AccountId && matchedOrder.RemainingQuantity > 0)
            {
                if (matchedOrder.RemainingQuantity >= quantityRemaining)
                {
                    matchedStatus = OrderStatus.Filled;
                    matchedOrder.RemainingQuantity -= quantityRemaining;
                    matchedOrder.Timestamp = DateTime.Now.Ticks;
                    order.Timestamp = matchedOrder.Timestamp;
                    order.RemainingQuantity = 0;
                    quantityFilled = quantityRemaining;
                }
                else
                {
                    quantityRemaining = matchedOrder.RemainingQuantity;
                    order.RemainingQuantity -= quantityRemaining;
                    order.Timestamp = DateTime.Now.Ticks;
                    matchedStatus = OrderStatus.PartiallyFilled;
                    matchedOrder.Timestamp = order.Timestamp;
                    matchedOrder.RemainingQuantity = 0;
                    quantityFilled = quantityRemaining;
                }

                NotifyOrderFillStatus(ref matchedOrder, matchedStatus);
                
                // Update price level data
                priceLevel.QuantityAvailable -= quantityRemaining;
                priceLevel.LastFillQuantity = quantityRemaining;
                priceLevel.LastFillTimestamp = DateTime.Now.Ticks;
                
                // If the bestOrder was completely filled, it needs to be removed from the orderbook
                if (matchedOrder.RemainingQuantity == 0)
                    _Orders.RemoveOrder(ref matchedOrder, ref priceLevel);
            }
        }

        return quantityFilled;
    }

    private bool AddOrderToBook(ref Order order)
    {
        PriceLevel priceLevel;
        int levelIndex;
        long price = order.Price;
        
        if (order.Side == OrderSide.Buy)
            priceLevel = _BidPriceLevels.GetPriceLevelByPrice(order.Price);
        else
            priceLevel = _AskPriceLevels.GetPriceLevelByPrice(order.Price);

        // If the price level doesn't exist, add it to the price levels
        if (Unsafe.IsNullRef(ref priceLevel))
        {
            levelIndex = _BidPriceLevels.AddPriceLevel(price, order.Side);
            priceLevel = _BidPriceLevels.GetPriceLevelByIndex(levelIndex);
        }

        priceLevel.QuantityAvailable += order.RemainingQuantity;
        priceLevel.OrderCount++; 

        if (_Orders.AddOrder(order, ref priceLevel) == -1)
            return false;

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsDuplicateOrder(long orderId)
    {
        return _Orders.FindOrderIndex(orderId) != -1;
    }
    
    public void Dispose()
    {
        _BidPriceLevels.Dispose();
        _AskPriceLevels.Dispose();
        _Orders.Dispose();
    }
}