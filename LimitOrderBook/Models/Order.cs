using System.Runtime.InteropServices;

namespace LimitOrderBook.Models;

[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct Order
{
    public long OrderId;
    public long Price;
    public byte Side;       // Need an enum for this
    public byte OrderType;  // Need an enum for this
    public int Quantity;
    public int RemainingQuantity;
    public byte OrderStatus;    // Need an enum for this
    public int PriceLevelIndex;

    public void Init(long orderId, long price, byte side, byte orderType, int quantity, int remainingQuantity,
        byte orderStatus, int priceLevelIndex)
    {
        OrderId = orderId;
        Price = price;
        Side = side;
        OrderType = orderType;
        Quantity = quantity;
        RemainingQuantity = remainingQuantity;
        OrderStatus = orderStatus;
        PriceLevelIndex = priceLevelIndex;
    }
}