using System.Buffers;
using System.Runtime.CompilerServices;
using LimitOrderBook.Models;

namespace LimitOrderBook.Services;

public class PriceLevelCollection : IDisposable
{
    private readonly int _Capacity;
    private readonly int _CapacityMask;
    private PriceLevel[] _Levels;
    private bool[] _Available;
    private int _CountInUse;
    private int _LastIndex;

    public PriceLevelCollection(int capacityPowerOf2)
    {
        if (capacityPowerOf2 > 0 && (capacityPowerOf2 & (capacityPowerOf2 - 1)) != 0) 
            throw new ArgumentException($"The {nameof(capacityPowerOf2)} parameter must be a power of 2.");
        
        _Capacity = capacityPowerOf2;
        _CapacityMask = _Capacity - 1;
        _CountInUse = 0;
        _LastIndex = 0;
        
        // Pre-allocate the pool and set available indices values to 1
        _Levels = new PriceLevel[_Capacity];
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
    public ref PriceLevel GetPriceLevelByPrice(long price, out int index)
    {
        index = FindLevelIndex(price);
        if (index == -1)
            return ref Unsafe.NullRef<PriceLevel>();

        return ref _Levels[index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref PriceLevel GetPriceLevelByIndex(int index)
    {
        if (index >= 0 && index < _Capacity && !_Available[index])
            return ref _Levels[index];
        
        return ref Unsafe.NullRef<PriceLevel>();
    }

    public int GetNextHigherPriceLevel(long price)
    {
        long nearestHigherPrice = long.MaxValue;
        int nextHigherIndex = -1;
            
        for (int index = 0; index < _Capacity; index++)
        {
            if (!_Available[index] && _Levels[index].Price > price)
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
            if (!_Available[index] && _Levels[index].Price < price)
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
            int higherLevel_LowerIndex = (nextHigherIndex == -1 ? -1 : _Levels[nextHigherIndex].NextLowerLevelIndex);
            
            // 3. Insert a new price level [NewLevel] and link HigherLevel.NextLowerIndex to its index
            int loopCount = 0;
            int searchIndex = _LastIndex;

            while (!_Available[searchIndex] && loopCount < _Capacity)
            {
                searchIndex = (searchIndex + 1) & _CapacityMask;
                loopCount++;
            }
            
            // 4. If an available slot was found, use it and mark it as used
            if (loopCount < _Capacity)
            {
                _LastIndex = searchIndex;
                _Levels[_LastIndex].Clear();
                _Levels[_LastIndex].Price = price;
                _Levels[_LastIndex].Side = side;

                // 5. Link NewLevel.NextHigherIndex to HigherLevel, and link NewLevel.NextLowerIndex to LowerLevel
                // Link LowerLevel.NextHigherIndex to NewLevel
                _Levels[_LastIndex].NextLowerLevelIndex = higherLevel_LowerIndex;
                _Levels[_LastIndex].NextHigherLevelIndex = nextHigherIndex;
                
                if (nextHigherIndex > -1)
                    _Levels[nextHigherIndex].NextLowerLevelIndex = _LastIndex;
                if (higherLevel_LowerIndex > -1)
                    _Levels[higherLevel_LowerIndex].NextHigherLevelIndex = _LastIndex;

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
            if (index != -1)
            {
                // 2. Link the HigherLevel.NextLowerIndex to LowerLevel index
                int nextHigherIndex = _Levels[index].NextHigherLevelIndex;
                int nextLowerIndex = _Levels[index].NextLowerLevelIndex;
                
                if (nextHigherIndex != -1)
                    _Levels[nextHigherIndex].NextLowerLevelIndex = nextLowerIndex;
                
                // 3. Link the LowerLevel.NextHigherIndex to HigherLevel index
                if (nextLowerIndex != -1)
                    _Levels[nextLowerIndex].NextHigherLevelIndex = nextHigherIndex;

                // 4. Clear the CurrentLevel item content
                _Levels[index].Clear();

                // 5. Mark NewLevel's AvailableIndex as true and decrement CountInUse
                _Available[index] = true;
                _LastIndex = index;
                _CountInUse--;

                return true;
            }
        }

        return false;
    }
    
    public void Dispose()
    {
        _Levels = null;
    }
}