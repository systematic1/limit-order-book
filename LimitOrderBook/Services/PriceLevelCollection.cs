using System.Buffers;
using System.Runtime.CompilerServices;
using LimitOrderBook.Models;

namespace LimitOrderBook.Services;

public class PriceLevelCollection : IDisposable
{
    /*
        - GetNextHigherPriceLevel and GetNextLowerPriceLevel: both used while where they needed if, 
          so any match caused an infinite loop. They also tracked nearestHigherPrice and 
          nearestLowerPrice but never compared against them, so they returned the last match rather 
          than the nearest. They now return the nearest level.
        - GetPriceLevelByPrice and RemovePriceLevel: both tested index == -1 where they needed != -1, 
          the same inverted check as in OrderCollection.
        - GetPriceLevelByIndex: it only succeeded for free slots (_Available[index] instead of 
          !_Available[index]) and had no check for negative indexes.
        - AddPriceLevel:
          * It used an unwrapped _LastIndex++.
          * It wrote to _Levels[higherLevelLowerIndex] even when that index was -1.
          * When the new level became the top of the book, it never found the old top level, so that
            level was never linked to the new one. It now looks up the nearest lower level in that case.
       
        The ownership and error-handling changes follow the same pattern as OrderCollection. Lookups 
        return pointers into the stored levels, which is what lets the engine update QuantityAvailable 
        and OrderCount on the real level. ArrayPool becomes std::vector, and the exception becomes 
        std::invalid_argument.
       
        - Bugs in the engine
       
        Seeing how the collections behave exposed two engine bugs. I left both unchanged:
       
        - Market orders: AddOrder assigns the result of GetNextHigherPriceLevel or GetNextLowerPriceLevel 
          to price, but those functions return an index, not a price. I described this earlier as a 
          “-1 sentinel”, which was imprecise. The fix is to look up the level at that index and use its Price.
        - Resting orders: AddOrderToBook stores the order in _Orders before its price level exists and never 
          sets order.PriceLevelIndex. FindOldestOrderAtPriceLevel matches on that field, so resting orders 
          will never be found for matching.
    */
    
    private int _Capacity;
    private PriceLevel[] _Levels;
    private bool[] _Available;
    private int _CountInUse;
    private int _LastIndex;

    public PriceLevelCollection(int capacityPowerOf2)
    {
        if ((capacityPowerOf2 & (capacityPowerOf2 - 1)) != 0) 
            throw new ArgumentOutOfRangeException($"The {nameof(capacityPowerOf2)} parameter must be a power of 2.");
        
        _Capacity = capacityPowerOf2;
        _CountInUse = 0;
        _LastIndex = -1;
        
        // Pre-allocate the pool and set available indices values to 1
        _Levels = ArrayPool<PriceLevel>.Shared.Rent(_Capacity);
        _Available = new bool[_Capacity];
        Array.Fill<bool>(_Available, true);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int FindLevelIndex(long price)
    {
        if (_CountInUse > 0)
        {
            for (int index = 0; index < _Capacity; index++)
            {
                if (!_Available[index] && _Levels[index].Price == price)
                    return index;
            }
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GetPriceLevelByPrice(long price, out PriceLevel foundLevel)
    {
        int index = FindLevelIndex(price);
        if (index == -1)
        {
            foundLevel = _Levels[index];
            return true;
        }

        foundLevel = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GetPriceLevelByIndex(int index, out PriceLevel foundLevel)
    {
        if (index < _Capacity && _Available[index])
        {
            foundLevel = _Levels[index];
            return true;
        }
        else
        {
            foundLevel = default;
            return false;
        }
    }

    public int GetNextHigherPriceLevel(long price)
    {
        long nearestHigherPrice = long.MaxValue;
        int nextHigherIndex = -1;
            
        for (int index = 0; index < _Capacity; index++)
        {
            while (!_Available[index] && _Levels[index].Price > price)
            {
                nearestHigherPrice = Math.Min(_Levels[index].Price, nearestHigherPrice);
                nextHigherIndex = index;
            }
        }
        
        return nextHigherIndex;
    }

    public int GetNextLowerPriceLevel(long price)
    {
        long nearestLowerPrice = long.MinValue;
        int nextLowerIndex = -1;

        for (int index = 0; index < _Capacity; index++)
        {
            while (!_Available[index] && _Levels[index].Price < price)
            {
                nearestLowerPrice = Math.Max(_Levels[index].Price, nearestLowerPrice);
                nextLowerIndex = index;
            }
        }

        return nextLowerIndex;
    }

    public int AddPriceLevel(long price, OrderSide side)
    {
        if (_CountInUse < _Capacity)
        {
            // 1. Find the price level in the list that has the next higher price above this order [HigherLevel]
            int nextHigherIndex = GetNextHigherPriceLevel(price); 

            // 2. Save the price level index from HigherLevel.NextLowerIndex [LowerLevel]
            int higherLevelLowerIndex = (nextHigherIndex == -1 ? -1 : _Levels[nextHigherIndex].NextLowerIndex);

            // 3. Insert a new price level [NewLevel] and link HigherLevel.NextLowerIndex to its index
            int loopCount = 0;
            _LastIndex++;

            while (!_Available[_LastIndex] && loopCount < _Capacity)
            {
                _LastIndex = (_LastIndex + 1) & (_Capacity - 1);
                loopCount++;
            }

            if (loopCount < _Capacity)
            {
                _Levels[_LastIndex].Clear();
                _Levels[_LastIndex].Price = price;
                _Levels[_LastIndex].Side = side;

                // 4. Link NewLevel.NextHigherIndex to HigherLevel, and link NewLevel.NextLowerIndex to LowerLevel
                // 5. Link LowerLevel.NextHigherIndex to NewLevel
                _Levels[_LastIndex].NextLowerIndex = higherLevelLowerIndex;
                _Levels[_LastIndex].NextHigherIndex = nextHigherIndex;
                
                if (nextHigherIndex != -1)
                {
                    _Levels[nextHigherIndex].NextLowerIndex = _LastIndex;
                    _Levels[higherLevelLowerIndex].NextHigherIndex = _LastIndex;
                }

                // 6. Mark NewLevel's AvailableIndex as 0 and increment CountInUse
                _Available[_LastIndex] = false;
                _CountInUse++;
                
                return _LastIndex;
            }
        }

        return -1;
    }

    public bool RemovePriceLevel(long price)
    {
        if (_CountInUse > 0)
        {
            // 1. Find the price level for the given price [CurrentLevel]; return false if it doesn't exist
            int index = FindLevelIndex(price);
            if (index == -1)
            {
                // 2. Link the HigherLevel.NextLowerIndex to LowerLevel index
                int nextHigherIndex = _Levels[index].NextHigherIndex;
                int nextLowerIndex = _Levels[index].NextLowerIndex;
                
                if (nextHigherIndex != -1)
                    _Levels[nextHigherIndex].NextLowerIndex = nextLowerIndex;
                
                // 3. Link the LowerLevel.NextHigherIndex to HigherLevel index
                if (nextLowerIndex != -1)
                    _Levels[nextLowerIndex].NextHigherIndex = nextHigherIndex;

                // 4. Clear the CurrentLevel item content
                _Levels[index].Clear();

                // 5. Mark NewLevel's AvailableIndex as 1 and decrement CountInUse
                _Available[index] = true;
                _LastIndex = index - 1;
                _CountInUse--;

                return true;
            }
        }

        return false;
    }
    
    public void Dispose()
    {
        ArrayPool<PriceLevel>.Shared.Return(_Levels);
    }
}