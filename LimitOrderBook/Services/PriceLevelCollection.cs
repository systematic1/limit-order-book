using System.Buffers;
using System.Runtime.CompilerServices;
using LimitOrderBook.Models;

namespace LimitOrderBook.Services;

public class PriceLevelCollection : IDisposable
{
    private readonly int Capacity;
    private readonly int CapacityMask;
    private PriceLevel[] Levels;
    private bool[] Available;
    private int CountInUse;
    private int LastIndex;
    private int HighestIndex;

    public PriceLevelCollection(int capacityPowerOf2)
    {
        if (capacityPowerOf2 > 0 && (capacityPowerOf2 & (capacityPowerOf2 - 1)) != 0) 
            throw new ArgumentException($"The {nameof(capacityPowerOf2)} parameter must be a power of 2.");
        
        Capacity = capacityPowerOf2;
        CapacityMask = Capacity - 1;
        CountInUse = 0;
        LastIndex = 0;
        HighestIndex = -1;
        
        // Pre-allocate the pool and set available indices values to 1
        Levels = new PriceLevel[Capacity];
        Available = new bool[Capacity];
        Array.Fill(Levels, new PriceLevel());
        Array.Fill(Available, true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int MaxIndex() => HighestIndex;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref PriceLevel LevelAtIndex(int index)
    {
        if (index >= 0 && index <= HighestIndex && !Available[index])
            return ref Levels[index];

        return ref Unsafe.NullRef<PriceLevel>();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int FindLevelIndex(long price)
    {
        if (CountInUse > 0)
        {
            for (int index = 0; index <= HighestIndex; index++)
            {
                if (!Available[index] && Levels[index].Price == price)
                    return index;
            }
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref PriceLevel GetPriceLevelByPrice(long price)
    {
        int index = FindLevelIndex(price);
        if (index == -1)
            return ref Unsafe.NullRef<PriceLevel>();

        return ref Levels[index];
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref PriceLevel GetPriceLevelByIndex(int index)
    {
        if (index >= 0 && index <= HighestIndex && !Available[index])
            return ref Levels[index];
        
        return ref Unsafe.NullRef<PriceLevel>();
    }

    public int GetNextHigherPriceLevel(long price)
    {
        long nearestHigherPrice = long.MaxValue;
        int nextHigherIndex = -1;
            
        for (int index = 0; index <= HighestIndex; index++)
        {
            if (!Available[index] && Levels[index].Price > price && Levels[index].Price < nearestHigherPrice)
            {
                nearestHigherPrice = Levels[index].Price;
                nextHigherIndex = index;
            }
        }
        
        return nextHigherIndex;
    }

    public int GetNextLowerPriceLevel(long price)
    {
        long nearestLowerPrice = long.MinValue;
        int nextLowerIndex = -1;

        for (int index = 0; index <= HighestIndex; index++)
        {
            if (!Available[index] && Levels[index].Price < price && Levels[index].Price > nearestLowerPrice)
            {
                nearestLowerPrice = Levels[index].Price;
                nextLowerIndex = index;
            }
        }

        return nextLowerIndex;
    }

    public int AddPriceLevel(long price, OrderSide side)
    {
        if (CountInUse < Capacity)
        {
            // 1. Find the price level in the list that has the next higher price above this order [HigherLevel]
            int nextHigherIndex = GetNextHigherPriceLevel(price);
            int nextLowerIndex = GetNextLowerPriceLevel(price);

            // 2. Save the price level index from HigherLevel.NextLowerIndex [LowerLevel]
            //int higherLevel_LowerIndex = (nextHigherIndex == -1 ? -1 : Levels[nextHigherIndex].NextLowerLevelIndex);
            
            // 3. Insert a new price level [NewLevel] and link HigherLevel.NextLowerIndex to its index
            int loopCount = 0;
            int searchIndex = LastIndex;

            while (!Available[searchIndex] && loopCount < Capacity)
            {
                searchIndex = (searchIndex + 1) & CapacityMask;
                loopCount++;
            }
            
            // 4. If an available slot was found, use it and mark it as used
            if (loopCount < Capacity)
            {
                LastIndex = searchIndex;
                Levels[LastIndex].Clear();
                Levels[LastIndex].Price = price;
                Levels[LastIndex].Side = side;

                // 5. Link NewLevel.NextHigherIndex to HigherLevel, and link NewLevel.NextLowerIndex to LowerLevel
                // Link LowerLevel.NextHigherIndex to NewLevel
                Levels[LastIndex].NextLowerLevelIndex = nextLowerIndex;  //higherLevel_LowerIndex;
                Levels[LastIndex].NextHigherLevelIndex = nextHigherIndex;
                
                if (nextHigherIndex != -1)
                    Levels[nextHigherIndex].NextLowerLevelIndex = LastIndex;
                if (nextLowerIndex != -1)
                    Levels[nextLowerIndex].NextHigherLevelIndex = LastIndex;
                
                /*if (nextHigherIndex > -1)
                    Levels[nextHigherIndex].NextLowerLevelIndex = LastIndex;
                if (higherLevel_LowerIndex > -1)
                    Levels[higherLevel_LowerIndex].NextHigherLevelIndex = LastIndex;*/

                // 6. Mark NewLevel's AvailableIndex as 0 and increment CountInUse
                Available[LastIndex] = false;
                CountInUse++;
                
                if (LastIndex > HighestIndex)
                    HighestIndex = LastIndex;
                
                return LastIndex;
            }
        }

        return -1;
    }

    public bool RemovePriceLevel(long price)
    {
        if (CountInUse > 0)
        {
            // 1. Find the price level for the given price [CurrentLevel]; return false if it doesn't exist
            int index = FindLevelIndex(price);
            if (index != -1)
            {
                // 2. Link the HigherLevel.NextLowerIndex to LowerLevel index
                int nextHigherIndex = Levels[index].NextHigherLevelIndex;
                int nextLowerIndex = Levels[index].NextLowerLevelIndex;
                
                if (nextHigherIndex != -1)
                    Levels[nextHigherIndex].NextLowerLevelIndex = nextLowerIndex;
                
                // 3. Link the LowerLevel.NextHigherIndex to HigherLevel index
                if (nextLowerIndex != -1)
                    Levels[nextLowerIndex].NextHigherLevelIndex = nextHigherIndex;

                // 4. Clear the CurrentLevel item content
                Levels[index].Clear();

                // 5. Mark NewLevel's AvailableIndex as true and decrement CountInUse
                Available[index] = true;
                LastIndex = index;
                CountInUse--;

                if (index == HighestIndex)
                    HighestIndex--;

                return true;
            }
        }

        return false;
    }
    
    public void Dispose()
    {
        Levels = null;
    }
}