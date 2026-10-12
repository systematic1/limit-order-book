using System.Buffers;
using System.Runtime.CompilerServices;
using LimitOrderBook.Models;

namespace LimitOrderBook.Services;

public class OrderCollection : IDisposable
{
    /*
        -- Second pass Claude analysis --
        
       Correctness bugs
       
       * AddOrder corrupts the time-priority list. It sets _Orders[priceLevel.NewestOrderIndex].NextOlderAtLevelIndex = _LastIndex. The previous newest order needs its newer link pointed at the new slot. As written, its older link is overwritten, so the chain is broken after the second order at a level. Your own comment (NextAtLevel = slot) shows the intended behavior.
       * RemoveOrder can throw IndexOutOfRangeException. _LastIndex = index - 1 gives -1 when slot 0 is freed. The next _Available[_LastIndex] in AddOrder then fails.
       * FindBestAtPriceLevel never returns what it searches for.
       * PriceLevel nextLevel = priceLevel; copies the struct, so Unsafe.IsNullRef(ref nextLevel) can never be true.
       * nextLevel = ... Unsafe.NullRef<PriceLevel>() assigns through a null ref, which is a NullReferenceException. It needs ref locals and nextLevel = ref ....
       * After the loop, the code searches priceLevel instead of nextLevel, so the skip-empty-levels walk has no effect.
       * Constructor validation is wrong.
       * 0 passes the power-of-2 check, and so do negative values that happen to match the bit pattern.
       * new ArgumentOutOfRangeException(message) passes the message as paramName.
       * There is no duplicate order-ID check. Two orders with the same ID can be added, and FindOrderIndex then finds only the first.
       
       Design and performance
       
       * FindOrderIndex is O(n), and RemoveOrder calls it. That undermines the O(1) linked-list design. Use an orderId → slot dictionary or an open-addressing hash. AggressiveInlining on a loop does nothing useful.
       * _LastIndex slot hunting is O(n) worst case and fragile. A free-list stack gives O(1) allocation and removes the index-minus-one trap.
       * RemoveOrder takes an Order just to re-search by ID. Accept the slot index and avoid the second lookup.
       * Nothing checks that the order belongs to the priceLevel you pass in. A mismatch silently corrupts a different level’s head or tail. At least add a debug assertion.
       * ArrayPool<Order>.Rent is a poor fit for a long-lived pool.
       * It may return a larger array than requested.
       * It returns uncleared memory.
       * Dispose isn’t idempotent, so a double return is a bug.
       * A plain new Order[n] is simpler.
       * Slot handles can go stale. A reused slot (the ABA problem) lets an old handle point at a new order. Consider a generation counter in the handle.
       *Unsafe.NullRef as a “not found” signal is a footgun. Callers must remember Unsafe.IsNullRef. A bool TryGet(out ...) or an index return is safer.

       Housekeeping:

       * Fields that never change should be readonly.
       * The /* CLAUDE.AI * / blocks, the stale “set available indices to 1” comment and the dead “OLD CODE” block should go.
       * FindBestAtPriceLevel should take ref readonly PriceLevel.
       * matchSide == Buy → NextLowerIndex is easy to misread, so document which side’s book it walks.
       * The class has no thread-safety note, and it exposes no Count or Capacity.
       
       What the Rust port changes
       
       * Bug fixes. All the bugs above are fixed, and each fix is marked // FIX: in the code.
       * Free list and ID map. Allocation and removal use a free-list stack and a HashMap<i64, Slot>, so both are O(1). The power-of-2 requirement is gone because nothing masks indexes anymore. For production, swap the std hasher for rustc-hash or ahash.
       * Safer API. The API returns Option, Result<Slot, AddOrderError> (Full or DuplicateId) and Option<Order> on removal instead of null refs and sentinel returns. NIL = u32::MAX stands in for -1 inside the links, which keeps Order compact.
       * find_best_at_price_level takes a start index. It takes a level index rather than &mut PriceLevel, because the starting level lives inside the same collection and the borrow checker would reject aliasing it.
       * No Dispose. Vectors free themselves on Drop.
       * Debug assertions. debug_assert! checks that a removed order really is the head or tail of the supplied level when its neighbor link is NIL.
    */
    
    private int _Capacity;
    private int _CapacityMask;
    private Order[] _Orders;
    private bool[] _Available;
    private int _CountInUse;
    private int _LastIndex;

    public OrderCollection(int capacityPowerOf2)
    {
        if ((capacityPowerOf2 & (capacityPowerOf2 - 1)) != 0) 
            throw new ArgumentOutOfRangeException($"The {nameof(capacityPowerOf2)} parameter must be a power of 2.");
        
        _Capacity = capacityPowerOf2;
        _CapacityMask = _Capacity - 1;
        _CountInUse = 0;
        _LastIndex = 0;
        
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
        if (_CountInUse < _Capacity)
        {
            int loopCount = 0;

            while (!_Available[_LastIndex] && loopCount < _Capacity)
            {
                IncrementLastIndex();
                loopCount++;
            }

            // If we found an open slot, then use it and mark it used
            if (loopCount < _Capacity)
            {
                _Orders[_LastIndex].InitFrom(order);
                _Available[_LastIndex] = false;
                _CountInUse++;
                
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
        if (_CountInUse > 0)
        {
            int index = FindOrderIndex(order.OrderId);
            if (index != -1)
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
                
                // Clear it out and mark its slot as available
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
        
        while (!Unsafe.IsNullRef(ref nextLevel) && nextLevel.IsEmpty())
        {
            nextIndex = (matchSide == OrderSide.Buy ? nextLevel.NextLowerIndex : nextLevel.NextHigherIndex);
            nextLevel = (nextIndex != -1 ? levels.GetPriceLevelByIndex(nextIndex) : Unsafe.NullRef<PriceLevel>());
        }
        
        if (!Unsafe.IsNullRef(ref priceLevel))
            return ref FindOldestOrderAtPriceLevel(ref priceLevel);
        else
            return ref Unsafe.NullRef<Order>();    
    }

    private ref Order FindOldestOrderAtPriceLevel(ref readonly PriceLevel priceLevel)
    {
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
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void IncrementLastIndex() => _LastIndex = (_LastIndex + 1) & _CapacityMask;
    
    public void Dispose()
    {
        ArrayPool<Order>.Shared.Return(_Orders);
    }
}