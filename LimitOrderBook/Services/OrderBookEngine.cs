using System.Buffers;
using System.Runtime.CompilerServices;
using LimitOrderBook.Models;

namespace LimitOrderBook.Services;

public class OrderBookEngine : IDisposable
{
    private OrderCollection _Orders;
    private PriceLevelCollection _AskPriceLevels;
    private PriceLevelCollection _BidPriceLevels;
    
    public OrderBookEngine(int orderCapacity, int priceLevelCapacity)
    {
        _Orders = new OrderCollection(orderCapacity);
        _AskPriceLevels = new PriceLevelCollection(priceLevelCapacity);
        _BidPriceLevels = new PriceLevelCollection(priceLevelCapacity);
    }

    public OrderStatus CancelOrder(ref Order order)
    {
        OrderStatus status = ValidateOrder(ref order);
        if (status != OrderStatus.Unknown)
            return status;
        
        int orderIndex = _Orders.FindOrderIndex(order.OrderId);
        if (orderIndex == -1)
            return OrderStatus.NotFound;

        // Find the price level and adjust the quantity remaining and order count
        PriceLevel priceLevel;
        if (order.Side == OrderSide.Buy)
        {
            if (!_BidPriceLevels.GetPriceLevelByPrice(order.Price, out priceLevel))
                return OrderStatus.NotFound;
                
            priceLevel.QuantityAvailable -= order.RemainingQuantity;
            priceLevel.OrderCount--;
        }
        else
        {
            if (!_AskPriceLevels.GetPriceLevelByPrice(order.Price, out priceLevel))
                return OrderStatus.NotFound;
                
            priceLevel.QuantityAvailable -= order.RemainingQuantity;
            priceLevel.OrderCount--;
        }
        
        // Remove the order from the order list
        order.PriceLevelIndex = -1;

        if (!_Orders.RemoveOrder(ref order))
            return OrderStatus.Rejected;
        
        return OrderStatus.Canceled;
    }

    public OrderStatus ModifyOrder(ref Order order)
    {
        return OrderStatus.Rejected;
    }

    // ======================================================
    
    private OrderStatus AddOrder(ref Order order)
    {
        PriceLevel priceLevel;
        int levelIndex;
        long price = order.Price;

        OrderStatus status = ValidateOrder(ref order);
        if (status != OrderStatus.Unknown)
            return status;

        if (DoesOrderViolateRiskCheck(ref order))
            return OrderStatus.Rejected;
        
        if (IsDuplicateOrder(order.OrderId))
            return OrderStatus.Duplicate;

        if (order.Side == OrderSide.Buy)
        {
            // If market order, find the lowest ask price available to start
            if (order.OrderType == OrderType.Market)
                price = _AskPriceLevels.GetNextHigherPriceLevel(long.MinValue);
            
            // We keep looking for more orders to fill until our order is completed
            while (order.RemainingQuantity > 0)
            {
                if (!_AskPriceLevels.GetPriceLevelByPrice(price, out priceLevel))
                {
                    levelIndex = _AskPriceLevels.GetNextLowerPriceLevel(price);
                    if (levelIndex == -1)   // If no more orders are available, jump out of loop here
                        break;

                    _AskPriceLevels.GetPriceLevelByIndex(levelIndex, out priceLevel);
                    order.PriceLevelIndex = levelIndex;
                }

                // Find a matching order and fill it
                FillMatchingOrder(ref priceLevel, ref order);
            }
        }
        else
        {
            // If market order, find the highest bid price available to start
            if (order.OrderType == OrderType.Market)
                price = _BidPriceLevels.GetNextLowerPriceLevel(long.MaxValue);

            // We keep looking for more orders to fill until our order is completed
            while (order.RemainingQuantity > 0)
            {
                if (!_BidPriceLevels.GetPriceLevelByPrice(price, out priceLevel))
                {
                    levelIndex = _BidPriceLevels.GetNextHigherPriceLevel(price);
                    if (levelIndex == -1)  // If no more orders are available, jump out of loop here
                        break;

                    _BidPriceLevels.GetPriceLevelByIndex(levelIndex, out priceLevel);
                    order.PriceLevelIndex = levelIndex;
                }
                    
                // Find a matching order and fill it
                FillMatchingOrder(ref priceLevel, ref order);
            }
        }

        // If we filled the entire order quantity, then return "filled" status
        if (order.RemainingQuantity == 0)
            return OrderStatus.Filled;
            
        order.Timestamp = DateTime.Now.Ticks;

        // If we run out of qualifying limit orders, store the order in the book (resting)
        if (AddOrderToBook(ref order))
            return OrderStatus.PartiallyFilled;
        else
            return OrderStatus.Rejected;
    }

    private OrderStatus ValidateOrder(ref Order order)
    {
        // Validate - Basic
        if (order.FirmId <= 0)
            return OrderStatus.Rejected;

        if (order.RemainingQuantity < 0)
            return OrderStatus.Rejected;

        if (order.TotalQuantity < order.RemainingQuantity)
            return OrderStatus.Rejected;

        if (order.TotalQuantity == 0)
            return OrderStatus.Rejected;
        
        return OrderStatus.Unknown;
    }

    private bool DoesOrderViolateRiskCheck(ref Order order)
    {
        // TO DO ................
        return false;
    }

    private void FillMatchingOrder(ref PriceLevel priceLevel, ref Order order)
    {
        // TO DO ................
    }

    private bool AddOrderToBook(ref Order order)
    {
        PriceLevel priceLevel;
        int levelIndex;
        long price = order.Price;

        if (_Orders.AddOrder(order) == -1)
            return false;
        
        if (order.Side == OrderSide.Buy)
        {
            // Find the matching price level or create it if not already found
            if (!_BidPriceLevels.GetPriceLevelByPrice(price, out priceLevel))
            {
                levelIndex = _BidPriceLevels.AddPriceLevel(price, order.Side);
                _BidPriceLevels.GetPriceLevelByIndex(levelIndex, out priceLevel);
            }
        }
        else
        {
            // Find the matching price level or create it if not already found
            if (!_AskPriceLevels.GetPriceLevelByPrice(price, out priceLevel))
            {
                levelIndex = _AskPriceLevels.AddPriceLevel(price, order.Side);
                _AskPriceLevels.GetPriceLevelByIndex(levelIndex, out priceLevel);
            }
        }

        priceLevel.QuantityAvailable += order.RemainingQuantity;
        priceLevel.OrderCount++; 

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