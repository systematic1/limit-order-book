using System.Runtime.InteropServices;

namespace LimitOrderBook.Models;

[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct Order
{
    public long OrderId;
    public long Price;
    public OrderSide Side;       // Need an enum for this
    public OrderType OrderType;  // Need an enum for this
    public int TotalQuantity;
    public int RemainingQuantity;
    public OrderStatus OrderStatus;    // Need an enum for this
    public int PriceLevelIndex;
    public long Timestamp;
    public TimeToLive TimeToLive;
    public int FirmId;
    public long AccountId;

    public Order()
    {
    }

    public void Init(long orderId, long price, OrderSide side, OrderType orderType, 
        int totalQuantity, int remainingQuantity, OrderStatus orderStatus, 
        int priceLevelIndex, long timestamp, TimeToLive timeToLive,
        int firmId, long accountId)
    {
        OrderId = orderId;
        Price = price;
        Side = side;
        OrderType = orderType;
        TotalQuantity = totalQuantity;
        RemainingQuantity = remainingQuantity;
        OrderStatus = OrderStatus.Unknown;
        PriceLevelIndex = priceLevelIndex;
        Timestamp = timestamp;
        TimeToLive = timeToLive;
        FirmId = firmId;
        AccountId = accountId;
    }

    public void Clear()
    {
        Init(0L, 0L, OrderSide.Unknown, OrderType.Unknown, 0,
            0, OrderStatus.Unknown, -1, 0, TimeToLive.GoodTilCanceled,
            -1, -1);
    }
}