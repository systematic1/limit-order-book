using System.Buffers;
using System.Runtime.CompilerServices;
using LimitOrderBook.Models;

namespace LimitOrderBook.Services;

public class OrderBookEngine : IDisposable
{
    private ClientNotificationService NotificationService;
    private OrderCollection Orders;
    private PriceLevelCollection AskPriceLevels;
    private PriceLevelCollection BidPriceLevels;
    
    public OrderBookEngine(int orderCapacity, int priceLevelCapacity)
    {
        NotificationService = new ClientNotificationService();
        Orders = new OrderCollection(orderCapacity);
        AskPriceLevels = new PriceLevelCollection(priceLevelCapacity);
        BidPriceLevels = new PriceLevelCollection(priceLevelCapacity);
    }

    public OrderStatus AddOrder(ref Order order)
    {
        ref PriceLevel priceLevel = ref Unsafe.NullRef<PriceLevel>();
        int levelIndex;
        int quantityFilled = 0;
        bool wasFilled = true;
        long price = order.Price;

        OrderStatus status = ValidateOrder(ref order);
        if (status != OrderStatus.Unknown)
            return status;

        if (DoesOrderViolateRisk(ref order))
            return OrderStatus.Rejected;
        
        if (order.Side == OrderSide.Buy)
        {
            // If market order, find the lowest ask price available to start
            levelIndex = AskPriceLevels.GetNextHigherPriceLevel(long.MinValue);
            if (levelIndex != -1)
                price = AskPriceLevels.GetPriceLevelByIndex(levelIndex).Price;

            // We keep looking for more orders to fill until our order is completed
            while (order.RemainingQuantity > 0 && wasFilled)
            {
                priceLevel = ref AskPriceLevels.GetPriceLevelByPrice(price);
                if (Unsafe.IsNullRef(ref priceLevel))
                {
                    levelIndex = AskPriceLevels.GetNextHigherPriceLevel(price);
                    if (levelIndex == -1)   // If no more orders are available, jump out of loop here
                        break;

                    priceLevel = ref AskPriceLevels.GetPriceLevelByIndex(levelIndex);
                }

                // Find a matching order and fill it
                quantityFilled = FillMatchingOrder(ref priceLevel, ref order);
                wasFilled = (quantityFilled > 0);

                if (priceLevel.IsEmpty())
                    AskPriceLevels.RemovePriceLevel(priceLevel.Price);
            }
        }
        else     // Sell OrderSide
        {
            // If market order, find the highest bid price available to start
            levelIndex = BidPriceLevels.GetNextLowerPriceLevel(long.MaxValue);
            if (levelIndex != -1)
                price = BidPriceLevels.GetPriceLevelByIndex(levelIndex).Price;

            // We keep looking for more orders to fill until our order is completed
            while (order.RemainingQuantity > 0 && wasFilled)
            {
                priceLevel = ref BidPriceLevels.GetPriceLevelByPrice(price);
                if (Unsafe.IsNullRef(ref priceLevel))
                {
                    levelIndex = BidPriceLevels.GetNextLowerPriceLevel(price);
                    if (levelIndex == -1)  // If no more orders are available, jump out of loop here
                        break;

                    priceLevel = ref BidPriceLevels.GetPriceLevelByIndex(levelIndex);
                }
                    
                // Find a matching order and fill it
                quantityFilled = FillMatchingOrder(ref priceLevel, ref order);
                wasFilled = (quantityFilled > 0);

                if (priceLevel.IsEmpty())
                    BidPriceLevels.RemovePriceLevel(priceLevel.Price);            }
        }

        order.Timestamp = DateTime.Now.Ticks;

        // If we filled the entire order quantity, then return "filled" status
        if (order.RemainingQuantity == 0)
            return OrderStatus.Filled;

        // If we couldn't fill any of a market order, reject it, otherwise partial fill it.
        if (order.OrderType == OrderType.Market)
            return quantityFilled > 0 ? OrderStatus.PartiallyFilled : OrderStatus.Rejected;
        
        // If we run out of qualifying limit orders, store the order in the book (resting)
        if (AddOrderToBook(ref order))
            return quantityFilled > 0 ? OrderStatus.PartiallyFilled : OrderStatus.AcceptedWaiting;

        return OrderStatus.Rejected;
    }

    public OrderStatus CancelOrder(long orderId)
    {
        ref Order storedOrderRef = ref Orders.GetOrder(orderId);
        if (Unsafe.IsNullRef(ref storedOrderRef))
            return OrderStatus.NotFound;

        ref PriceLevelCollection priceLevelsSide = ref BidPriceLevels; 
        if (storedOrderRef.Side == OrderSide.Sell)
            priceLevelsSide = ref AskPriceLevels;

        // Find the price level and adjust the quantity remaining and order count
        ref PriceLevel priceLevel = ref Unsafe.NullRef<PriceLevel>();
        priceLevel = ref priceLevelsSide.GetPriceLevelByPrice(storedOrderRef.Price);

        if (Unsafe.IsNullRef(ref priceLevel))
            return OrderStatus.NotFound;
            
        priceLevel.QuantityAvailable -= storedOrderRef.RemainingQuantity;
        priceLevel.OrderCount--;
        
        if (priceLevel.OrderCount <= 0)
            priceLevelsSide.RemovePriceLevel(storedOrderRef.Price);
        
        // Remove the order from the order list
        storedOrderRef.PriceLevelIndex = -1;

        if (!Orders.RemoveOrder(ref storedOrderRef, ref priceLevel))
            return OrderStatus.Rejected;
        
        return OrderStatus.Canceled;
    }

    public async ValueTask NotifyOrderFillStatus(FillResults fillResults)
    {
        try
        {
            await NotificationService.SendNotification(fillResults.AccountID, fillResults.OrderID, 
                fillResults.Status, fillResults.Message.AsMemory());
        }
        catch (Exception e)
        {
            Console.WriteLine("Error while notifying client.");
            Console.WriteLine(e.Message);
        }
    }
    
    public void SetStatusMessage(ref FillResults results)
    {
        switch (results.Status)
        {
            case OrderStatus.Canceled:
                results.Message = $"Order {results.OrderID} has been cancelled.";
                break;
            
            case OrderStatus.Duplicate:
                results.Message = $"This order ({results.OrderID}) has already been submitted.";
                break;
            
            case OrderStatus.Filled:
                results.Message = $"Order {results.OrderID} has been filled with quantity {results.FilledQuantity} at ${(float)results.FillPrice / 100f}.";
                break;
            
            case OrderStatus.NotFound:
                results.Message = $"This order ({results.OrderID}) is invalid.";
                break;
            
            case OrderStatus.PartiallyFilled:
                results.Message = $"Order {results.OrderID} has been partially filled with quantity {results.FilledQuantity} at ${(float)results.FillPrice / 100f}.";
                break;
            
            case OrderStatus.Rejected:
                results.Message = $"Order {results.OrderID} has been rejected for reason: {results.Message ?? "None"}";
                break;
            
            case OrderStatus.AcceptedWaiting:
                results.Message = $"Order {results.OrderID} has been accepted (waiting).";
                break;
            
            default:
                results.Message = $"Order status {results.Status.ToString()} was returned for order {results.OrderID}";
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref OrderCollection GetOrderCollection() => ref Orders;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref PriceLevelCollection GetAskPriceLevelCollection() => ref AskPriceLevels;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref PriceLevelCollection GetBidPriceLevelCollection() => ref BidPriceLevels;

    // =====================================================================================
    
    private OrderStatus ValidateOrder(ref Order order)
    {
        // Validate - Basic
        if (order.TotalQuantity < order.RemainingQuantity)
            return OrderStatus.Rejected;

        if (order.RemainingQuantity <= 0 || order.TotalQuantity <= 0)
            return OrderStatus.Rejected;

        if (order.OrderType != OrderType.Market && order.OrderType != OrderType.Limit)
            return OrderStatus.Rejected;

        if (order.Side != OrderSide.Sell && order.Side != OrderSide.Buy)
            return OrderStatus.Rejected;

        if (order.TimeToLive != TimeToLive.GoodTilCanceled)
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
        OrderSide matchSide = (order.Side == OrderSide.Sell ? OrderSide.Buy : OrderSide.Sell);
        OrderStatus matchedStatus = default;
        OrderStatus currentStatus = default;
        PriceLevelCollection priceLevels = (order.Side == OrderSide.Sell ? BidPriceLevels : AskPriceLevels);
        int quantityRemaining = order.RemainingQuantity;
        int quantityFilled = 0;
        int matchRemaining = 0;
        int currentRemaining = quantityRemaining;

        // Search the order list for the best available order by time-priority
        ref Order matchedOrder = ref Orders.FindBestAtPriceLevel(ref priceLevel, ref priceLevels, matchSide);
        if (Unsafe.IsNullRef(ref matchedOrder))
            return 0;
        
        matchRemaining = matchedOrder.RemainingQuantity;
        
        if (!Unsafe.IsNullRef(ref matchedOrder))
        {
            if (matchedOrder.AccountId != order.AccountId && matchedOrder.RemainingQuantity > 0)
            {
                if (matchedOrder.RemainingQuantity >= quantityRemaining)
                {
                    matchedStatus = (matchedOrder.RemainingQuantity == quantityRemaining ? OrderStatus.Filled : OrderStatus.PartiallyFilled);
                    currentStatus = OrderStatus.Filled;
                    matchedOrder.RemainingQuantity -= quantityRemaining;
                    matchedOrder.Timestamp = DateTime.Now.Ticks;
                    order.Timestamp = matchedOrder.Timestamp;
                    order.RemainingQuantity = 0;
                    quantityFilled = quantityRemaining;
                    quantityRemaining = order.RemainingQuantity;
                }
                else     // Not enough to completely fill order
                {
                    quantityFilled = matchedOrder.RemainingQuantity;
                    quantityRemaining -= quantityFilled;
                    order.RemainingQuantity = quantityRemaining;
                    order.Timestamp = DateTime.Now.Ticks;
                    matchedStatus = OrderStatus.PartiallyFilled;
                    currentStatus = OrderStatus.Filled;
                    matchedOrder.Timestamp = order.Timestamp;
                    matchedOrder.RemainingQuantity = 0;
                }

                FillResults matchResults = new FillResults()
                {
                    OrderID = matchedOrder.OrderId,
                    RequestedQuantity = matchRemaining,
                    FilledQuantity = quantityFilled,
                    RemainingQuantity = matchedOrder.RemainingQuantity,
                    FillPrice = priceLevel.Price,
                    AuxData = 0,
                    Message = null,
                    Status = matchedStatus,
                    AccountID = matchedOrder.AccountId
                };
                SetStatusMessage(ref matchResults);
                NotifyOrderFillStatus(matchResults).GetAwaiter().GetResult();

                FillResults currentResults = new FillResults()
                {
                    OrderID = order.OrderId,
                    RequestedQuantity = currentRemaining,
                    FilledQuantity = quantityFilled,
                    RemainingQuantity = order.RemainingQuantity,
                    FillPrice = priceLevel.Price,
                    AuxData = 0,
                    Message = null,
                    Status = currentStatus,
                    AccountID = order.AccountId
                };
                SetStatusMessage(ref currentResults);
                NotifyOrderFillStatus(currentResults).GetAwaiter().GetResult();
                
                // Update price level data
                priceLevel.QuantityAvailable -= quantityFilled;
                priceLevel.LastFillQuantity = quantityRemaining;
                priceLevel.LastFillTimestamp = DateTime.Now.Ticks;
                
                // If either order was completely filled, it needs to be removed from the orderbook
                if (matchedOrder.RemainingQuantity == 0)
                {
                    Orders.RemoveOrder(ref matchedOrder, ref priceLevel);
                    priceLevel.OrderCount--;
                }
            }
        }

        return quantityFilled;
    }

    private bool AddOrderToBook(ref Order order)
    {
        ref PriceLevel priceLevel = ref Unsafe.NullRef<PriceLevel>();
        int levelIndex;

        if (order.Side == OrderSide.Buy)
        {
            priceLevel = ref BidPriceLevels.GetPriceLevelByPrice(order.Price);
            
            // If the price level doesn't exist, add it to the price levels
            if (Unsafe.IsNullRef(ref priceLevel))
            {
                levelIndex = BidPriceLevels.AddPriceLevel(order.Price, order.Side);
                if (levelIndex == -1)
                    return false;
                
                priceLevel = ref BidPriceLevels.GetPriceLevelByIndex(levelIndex);
                order.PriceLevelIndex = levelIndex;
            }
        }
        else    // Sell OrderSide
        {
            priceLevel = ref AskPriceLevels.GetPriceLevelByPrice(order.Price);
            
            // If the price level doesn't exist, add it to the price levels
            if (Unsafe.IsNullRef(ref priceLevel))
            {
                levelIndex = AskPriceLevels.AddPriceLevel(order.Price, order.Side);
                if (levelIndex == -1)
                    return false;
                
                priceLevel = ref AskPriceLevels.GetPriceLevelByIndex(levelIndex);
                order.PriceLevelIndex = levelIndex;
            }
        }

        priceLevel.QuantityAvailable += order.RemainingQuantity;
        priceLevel.OrderCount++; 

        if (Orders.AddOrder(order, ref priceLevel) == -1)
            return false;

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsDuplicateOrder(long orderId)
    {
        return Orders.FindOrderIndex(orderId) != -1;
    }
    
    public void Dispose()
    {
        BidPriceLevels.Dispose();
        AskPriceLevels.Dispose();
        Orders.Dispose();
    }

    public bool EnableNotifications
    {
        get => NotificationService.Enabled;
        set => NotificationService.Enabled = value;
    }
}