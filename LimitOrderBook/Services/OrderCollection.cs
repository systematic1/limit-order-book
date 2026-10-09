using System.Buffers;
using System.Runtime.CompilerServices;
using LimitOrderBook.Models;

namespace LimitOrderBook.Services;

public class OrderCollection : IDisposable
{
    /*
     CLAUDE.AI:
             
        - FindOrderIndex and FindOldestOrderAtPriceLevel scan the whole array linearly, so every 
          AddOrder is O(capacity) because of the duplicate check. 
          
          -> Fix involves:
          
          √ Making PriceLevel aware of its oldest and newest order (time-priority)
            - Add OldestOrderIndex and NewestOrderIndex to PriceLevel that points to the Orders array.
          √ Making Order aware of its next newer and older order at the same price level
            - Add NextNewerAtLevelIndex and NextOlderAtLevelIndex to Order that points to the order index
              for the next newer order (in the same PriceLevel) and next older order (same level)
          √ Adding or removing an order would require updating all four values to keep proper linkage       
    */
    
    private int _Capacity;
    private Order[] _Orders;
    private bool[] _Available;
    private int _CountInUse;
    private int _LastIndex;

    public OrderCollection(int capacityPowerOf2)
    {
        if ((capacityPowerOf2 & (capacityPowerOf2 - 1)) != 0) 
            throw new ArgumentOutOfRangeException($"The {nameof(capacityPowerOf2)} parameter must be a power of 2.");
        
        _Capacity = capacityPowerOf2;
        _CountInUse = 0;
        _LastIndex = -1;
        
        // Pre-allocate the pool and set available indices values to 1
        _Orders = ArrayPool<Order>.Shared.Rent(_Capacity);
        _Available = new bool[_Capacity];
        Array.Fill<bool>(_Available, true);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int FindOrderIndex(long orderId)
    {
        if (_CountInUse > 0)
        {
            for (int index = 0; index < _Capacity; index++)
            {
                if (!_Available[index] && _Orders[index].OrderId == orderId)
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
        
        return ref _Orders[index];
    }

    public int AddOrder(Order order, ref PriceLevel priceLevel)
    {
        /*
         @@@ DONE @@@
         
        CLAUDE AI:
        
           _Orders[slot].NextAtLevel = -1;
           _Orders[slot].PrevAtLevel = level.TailIndex;

           if (level.TailIndex != -1)
               _Orders[level.TailIndex].NextAtLevel = slot;
           else
               level.HeadIndex = slot;          // level was empty

           level.TailIndex = slot;
 
        */
        
        if (_CountInUse < _Capacity)
        {
            int loopCount = 0;
            _LastIndex = (_LastIndex + 1) & (_Capacity - 1);

            while (!_Available[_LastIndex] && loopCount < _Capacity)
            {
                _LastIndex = (_LastIndex + 1) & (_Capacity - 1);
                loopCount++;
            }

            if (loopCount < _Capacity)
            {
                _Orders[_LastIndex].InitFrom(order);
                _Available[_LastIndex] = false;
                _CountInUse++;
                
                // Update order time-sort indices
                _Orders[_LastIndex].NextNewerAtLevelIndex = -1;
                _Orders[_LastIndex].NextOlderAtLevelIndex = priceLevel.NewestOrderIndex;
                
                // Update relevant PriceLevel newest/oldest order indices at that level
                if (priceLevel.NewestOrderIndex != -1)
                    _Orders[priceLevel.NewestOrderIndex].NextOlderAtLevelIndex = _LastIndex;
                else 
                    priceLevel.OldestOrderIndex = _LastIndex;
                
                priceLevel.NewestOrderIndex = _LastIndex;
                
                return _LastIndex;
            }
        }

        return -1;
    }

    public bool RemoveOrder(ref readonly Order order, ref PriceLevel priceLevel)
    {
        /*
         @@@ DONE @@@
         
        CLAUDE AI:
        
           int prev = _Orders[slot].PrevAtLevel;
           int next = _Orders[slot].NextAtLevel;

           if (prev != -1) 
               _Orders[prev].NextAtLevel = next;
           else
               level.HeadIndex = next;

           if (next != -1) 
               _Orders[next].PrevAtLevel = prev;
           else
               level.TailIndex = prev;
        */
        
        if (_CountInUse > 0)
        {
            int index = FindOrderIndex(order.OrderId);
            if (index != -1)
            {
                // Update newer/older order index pointers for same price level
                int olderIndex = _Orders[index].NextOlderAtLevelIndex;
                int newerIndex = _Orders[index].NextNewerAtLevelIndex;
                
                if (olderIndex != -1)
                    _Orders[olderIndex].NextNewerAtLevelIndex = newerIndex;
                else 
                    priceLevel.OldestOrderIndex = newerIndex;
                
                if (newerIndex != -1)
                    _Orders[newerIndex].NextOlderAtLevelIndex = olderIndex;
                else 
                    priceLevel.NewestOrderIndex = olderIndex;
                
                // Clear it out and mark it's slot as available
                _Orders[index].Clear();
                _Available[index] = true;
                _LastIndex = index - 1;
                _CountInUse--;

                return true;
            }
        }

        return false;
    }

    public ref Order FindBestAtPriceLevel(ref PriceLevel priceLevel, ref readonly PriceLevelCollection levels, 
        OrderSide matchSide)
    {
        // Search for the next order by date/time and price (ask=search higher, bid=search lower)
        // * Use the PriceLevel.NextHigherIndex or PriceLevel.NextLowerIndex
        // * Move to next price level and search for orders matching that price level index
        // * Find the oldest order

        PriceLevel nextLevel = priceLevel;
        int nextIndex;

        do
        {
            if (matchSide == OrderSide.Buy)
                nextIndex = nextLevel.NextLowerIndex;
            else
                nextIndex = nextLevel.NextHigherIndex;

            if (nextIndex != -1)
                nextLevel = levels.GetPriceLevelByIndex(nextIndex);
            else
                nextLevel = Unsafe.NullRef<PriceLevel>();

        } while (!Unsafe.IsNullRef(ref nextLevel) && nextLevel.IsEmpty());

        if (nextIndex != -1)
            return ref FindOldestOrderAtPriceLevel(ref priceLevel);
        else
            return ref Unsafe.NullRef<Order>();    
    }

    private ref Order FindOldestOrderAtPriceLevel(ref readonly PriceLevel priceLevel)
    {
        /*
         @@@ DONE @@@
         
        CLAUDE AI:
        
            if (level.HeadIndex != -1)
            {
                order = _Orders[level.HeadIndex];
                return true;
            }
            order = default;
            return false;
        */

        if (priceLevel.OldestOrderIndex != -1)
            return ref _Orders[priceLevel.OldestOrderIndex];
         
        return ref Unsafe.NullRef<Order>();
        
        /*
        // OLD CODE:
         
        long oldestTicks = DateTime.Now.Ticks;
        int oldestIndex = -1;
        
        for (int index = 0; index < _Capacity; index++)
        {
            if (!_Available[index] &&
                _Orders[index].PriceLevelIndex == priceLevelIndex &&
                oldestTicks > _Orders[index].Timestamp)
            {
                oldestTicks = _Orders[index].Timestamp;
                oldestIndex = index;                    
            }
        }

        if (oldestIndex == -1)
            return ref Unsafe.NullRef<Order>();
        
        return ref _Orders[oldestIndex];*/
    }
    
    public void Dispose()
    {
        ArrayPool<Order>.Shared.Return(_Orders);
    }
}