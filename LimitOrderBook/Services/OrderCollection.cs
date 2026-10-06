using System.Buffers;
using System.Runtime.CompilerServices;
using LimitOrderBook.Models;

namespace LimitOrderBook.Services;

public class OrderCollection : IDisposable
{
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
    public bool GetOrder(long orderId, out Order foundOrder)
    {
        int index = FindOrderIndex(orderId);
        if (index == -1)
        {
            foundOrder = _Orders[index];
            return true;
        }

        foundOrder = default;
        return false;
    }

    public int AddOrder(Order order)
    {
        if (_CountInUse < _Capacity)
        {
            int loopCount = 0;
            _LastIndex++;

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
                
                return _LastIndex;
            }
        }

        return -1;
    }

    public bool RemoveOrder(ref Order order)
    {
        if (_CountInUse > 0)
        {
            int index = FindOrderIndex(order.OrderId);
            if (index == -1)
            {
                _Orders[index].Clear();
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
        ArrayPool<Order>.Shared.Return(_Orders);
    }
}