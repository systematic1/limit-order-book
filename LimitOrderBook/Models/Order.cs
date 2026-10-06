using System.Runtime.InteropServices;

namespace LimitOrderBook.Models;

[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct Order
{
    public long OrderId;
    public long Price;
    public OrderSide Side;       
    public OrderType OrderType;  
    public int TotalQuantity;
    public int RemainingQuantity;
    public int PriceLevelIndex;
    public long Timestamp;
    public TimeToLive TimeToLive;
    public int FirmId;
    public long AccountId;
    public long SecurityId;

    public Order()
    {
    }

    public void Init(long orderId, long price, OrderSide side, OrderType orderType, 
        int totalQuantity, int remainingQuantity, int priceLevelIndex, long timestamp, 
        TimeToLive timeToLive, int firmId, long accountId, long securityId)
    {
        OrderId = orderId;
        Price = price;
        Side = side;
        OrderType = orderType;
        TotalQuantity = totalQuantity;
        RemainingQuantity = remainingQuantity;
        PriceLevelIndex = priceLevelIndex;
        Timestamp = timestamp;
        TimeToLive = timeToLive;
        FirmId = firmId;
        AccountId = accountId;
        SecurityId = securityId;
    }

    public void InitFrom(Order source)
    {
        Init(source.OrderId, source.Price, source.Side, source.OrderType, source.TotalQuantity,
            source.RemainingQuantity, source.PriceLevelIndex, source.Timestamp,
            source.TimeToLive, source.FirmId, source.AccountId, source.SecurityId);
    }

    public void Clear()
    {
        Init(0L, 0L, OrderSide.Buy, OrderType.Limit, 0,
            0, -1, 0, TimeToLive.GoodTilCanceled,
            -1, -1L, -1L);
    }
}