using System.Buffers;
using System.Runtime.CompilerServices;
using LimitOrderBook.Models;

namespace LimitOrderBook.Services;

public class OrderCollection : IDisposable
{
    /*
       [-] <This requires allocating memory at RT> FindOrderIndex is O(n), and RemoveOrder calls it. That undermines the O(1) linked-list design. Use an orderId → slot dictionary or an open-addressing hash. AggressiveInlining on a loop does nothing useful.
       [-] _LastIndex slot hunting is O(n) worst case and fragile. A free-list stack gives O(1) allocation and removes the index-minus-one trap.
       [-] RemoveOrder takes an Order just to re-search by ID. Accept the slot index and avoid the second lookup.
       [-] Slot handles can go stale. A reused slot (the ABA problem) lets an old handle point at a new order. Consider a generation counter in the handle.
       [-] Unsafe.NullRef as a “not found” signal is a footgun. Callers must remember Unsafe.IsNullRef. A bool TryGet(out ...) or an index return is safer.
       [ ] The class has no thread-safety note, and it exposes no Count or Capacity.
            [ ] Use "Interlocked.Increment" and "Interlocked.CompareAndExchange"
    */
    
    private readonly int _Capacity;
    private readonly int _CapacityMask;
    private Order[] _Orders;
    private bool[] _Available;
    private int _CountInUse;
    private int _LastIndex;

    public OrderCollection(int capacityPowerOf2)
    { 
        if (capacityPowerOf2 > 0 && (capacityPowerOf2 & (capacityPowerOf2 - 1)) != 0) 
            throw new ArgumentException($"The {nameof(capacityPowerOf2)} parameter must be a positive power of 2.");
        
        _Capacity = capacityPowerOf2;
        _CapacityMask = _Capacity - 1;
        _CountInUse = 0;
        _LastIndex = 0;
        
        // Pre-allocate the pool and set available indices values to true
        _Orders = new Order[_Capacity];
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
        if (_CountInUse < _Capacity)
        {
            if (FindOrderIndex(order.OrderId) != -1)
                return -1;

            if (priceLevel.Price != order.Price)
                return -1;
            
            int loopCount = 0;
            int searchIndex = _LastIndex;

            while (!_Available[searchIndex] && loopCount < _Capacity)
            {
                searchIndex = (searchIndex + 1) & _CapacityMask;
                loopCount++;
            }

            // If we found an open slot, then use it and mark it used
            if (loopCount < _Capacity)
            {
                _LastIndex = searchIndex;
                _Orders[_LastIndex].InitFrom(order);
                _Available[_LastIndex] = false;
                _CountInUse++;
                
                // Update order time-sort indices
                _Orders[_LastIndex].NextNewerOrderIndexAtLevel = -1;
                _Orders[_LastIndex].NextOlderOrderIndexAtLevel = priceLevel.NewestOrderIndex;
                
                // Update relevant PriceLevel newest/oldest order indices at that level
                if (priceLevel.NewestOrderIndex != -1)
                    _Orders[priceLevel.NewestOrderIndex].NextNewerOrderIndexAtLevel = _LastIndex;
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
        if (order.Price != priceLevel.Price)
            return false;
        
        if (_CountInUse > 0)
        {
            int index = FindOrderIndex(order.OrderId);
            if (index != -1)
            {
                // Update newer/older order index pointers for same price level
                int olderIndex = _Orders[index].NextOlderOrderIndexAtLevel;
                int newerIndex = _Orders[index].NextNewerOrderIndexAtLevel;
                
                if (olderIndex != -1)
                    _Orders[olderIndex].NextNewerOrderIndexAtLevel = newerIndex;
                else 
                    priceLevel.OldestOrderIndex = newerIndex;
                
                if (newerIndex != -1)
                    _Orders[newerIndex].NextOlderOrderIndexAtLevel = olderIndex;
                else 
                    priceLevel.NewestOrderIndex = olderIndex;
                
                // Clear it out and mark its slot as available
                _Orders[index].Clear();
                _Available[index] = true;
                _LastIndex = index > 0 ? index - 1 : 0; 
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

        ref PriceLevel nextLevel = ref priceLevel;
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
            if (nextLevel.OldestOrderIndex < _Capacity && !_Available[priceLevel.OldestOrderIndex])
                return ref _Orders[priceLevel.OldestOrderIndex];
        }

        return ref Unsafe.NullRef<Order>();    
    }
    
    public void Dispose()
    {
        _Orders = null;
    }
}