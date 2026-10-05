using System.Runtime.InteropServices;

namespace LimitOrderBook.Models;

[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct PriceLevel
{
    public long Price;
    public long QuantityAvailable;
    public int OrderCount;
    public OrderSide Side;
    public long LastFillQuantity;
    public long LastFillTimestamp;

    public int NextHigherIndex = -1;
    public int NextLowerIndex = -1;

    public PriceLevel()
    {
    }
    
    public void Init(long price, long quantity, int orderCount, OrderSide side)
    {
        Price = price;
        QuantityAvailable = quantity;
        OrderCount = orderCount;
        Side = side;
        LastFillQuantity = 0L;
        LastFillTimestamp = 0L;
    }

    public void Clear()
    {
        Init(0L, 0L, 0, OrderSide.Unknown);
    }

    public bool IsEmpty()
    {
        return QuantityAvailable == 0L;
    }
}