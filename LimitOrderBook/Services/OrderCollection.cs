using System.Buffers;
using System.Runtime.CompilerServices;
using LimitOrderBook.Models;

namespace LimitOrderBook.Services;

public class OrderCollection : IDisposable
{
    private readonly int Capacity;
    private readonly int CapacityMask;
    private Order[] Orders;
    private bool[] Available;
    private int CountInUse;
    private int LastIndex;
    private int HighestIndex;

    public OrderCollection(int capacityPowerOf2)
    { 
        if (capacityPowerOf2 > 0 && (capacityPowerOf2 & (capacityPowerOf2 - 1)) != 0) 
            throw new ArgumentException($"The {nameof(capacityPowerOf2)} parameter must be a positive power of 2.");
        
        Capacity = capacityPowerOf2;
        CapacityMask = Capacity - 1;
        CountInUse = 0;
        LastIndex = 0;
        HighestIndex = 0;
        
        // Pre-allocate the pool and set available indices values to true
        Orders = new Order[Capacity];
        Available = new bool[Capacity];
        Array.Fill<bool>(Available, true);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int FindOrderIndex(long orderId)
    {
        if (CountInUse > 0)
        {
            for (int index = 0; index <= HighestIndex; index++)
            {
                if (!Available[index] && Orders[index].OrderId == orderId)
                    return index;
            }
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref Order GetOrder(long orderId)
    {
        int index = FindOrderIndex(orderId);
        if (index == -1)
            return ref Unsafe.NullRef<Order>();
        
        return ref Orders[index];
    }

    public int AddOrder(Order order, ref PriceLevel priceLevel)
    {
        if (CountInUse < Capacity)
        {
            if (FindOrderIndex(order.OrderId) != -1)
                return -1;

            if (priceLevel.Price != order.Price)
                return -1;
            
            int loopCount = 0;
            int searchIndex = LastIndex;

            while (!Available[searchIndex] && loopCount < Capacity)
            {
                searchIndex = (searchIndex + 1) & CapacityMask;
                loopCount++;
            }

            // If we found an open slot, then use it and mark it used
            if (loopCount < Capacity)
            {
                LastIndex = searchIndex;
                Orders[LastIndex].InitFrom(order);
                Available[LastIndex] = false;
                CountInUse++;
                
                // Update order time-sort indices
                Orders[LastIndex].NextNewerOrderIndexAtLevel = -1;
                Orders[LastIndex].NextOlderOrderIndexAtLevel = priceLevel.NewestOrderIndex;
                
                // Update relevant PriceLevel newest/oldest order indices at that level
                if (priceLevel.NewestOrderIndex != -1)
                    Orders[priceLevel.NewestOrderIndex].NextNewerOrderIndexAtLevel = LastIndex;
                else 
                    priceLevel.OldestOrderIndex = LastIndex;
                
                priceLevel.NewestOrderIndex = LastIndex;

                if (LastIndex > HighestIndex)
                    HighestIndex = LastIndex;
                
                return LastIndex;
            }
        }

        return -1;
    }

    public bool RemoveOrder(ref readonly Order order, ref PriceLevel priceLevel)
    {
        if (order.Price != priceLevel.Price)
            return false;
        
        if (CountInUse > 0)
        {
            int index = FindOrderIndex(order.OrderId);
            if (index != -1)
            {
                // Update newer/older order index pointers for same price level
                int olderIndex = Orders[index].NextOlderOrderIndexAtLevel;
                int newerIndex = Orders[index].NextNewerOrderIndexAtLevel;
                
                if (olderIndex != -1)
                    Orders[olderIndex].NextNewerOrderIndexAtLevel = newerIndex;
                else 
                    priceLevel.OldestOrderIndex = newerIndex;
                
                if (newerIndex != -1)
                    Orders[newerIndex].NextOlderOrderIndexAtLevel = olderIndex;
                else 
                    priceLevel.NewestOrderIndex = olderIndex;
                
                // Clear it out and mark its slot as available
                Orders[index].Clear();
                Available[index] = true;
                LastIndex = index > 0 ? index - 1 : 0; 
                CountInUse--;

                if (index == HighestIndex)
                    HighestIndex--;

                return true;
            }
        }

        return false;
    }

    public ref Order FindBestAtPriceLevel(ref PriceLevel nextLevel, ref readonly PriceLevelCollection levels, 
        OrderSide matchSide)
    {
        // Search for the next order by date/time and price (ask=search higher, bid=search lower)
        // * Use the PriceLevel.NextHigherIndex or PriceLevel.NextLowerIndex
        // * Move to next price level and search for orders matching that price level index
        // * Find the oldest order

        //ref PriceLevel nextLevel = ref priceLevel;
        int nextIndex;
        
        while (!Unsafe.IsNullRef(ref nextLevel) && nextLevel.IsEmpty())
        {
            nextIndex = (matchSide == OrderSide.Buy ? nextLevel.NextLowerLevelIndex : nextLevel.NextHigherLevelIndex);
            if (nextIndex != -1)
                nextLevel = ref levels.GetPriceLevelByIndex(nextIndex);
            else
                nextLevel = ref Unsafe.NullRef<PriceLevel>();
        }

        if (!Unsafe.IsNullRef(ref nextLevel) && nextLevel.OldestOrderIndex != -1)
        {
            if (nextLevel.OldestOrderIndex < Capacity && !Available[nextLevel.OldestOrderIndex])
                return ref Orders[nextLevel.OldestOrderIndex];
        }

        return ref Unsafe.NullRef<Order>();    
    }
    
    public void Dispose()
    {
        Orders = null;
    }
}