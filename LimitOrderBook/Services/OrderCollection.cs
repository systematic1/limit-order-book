using System.Buffers;
using System.Runtime.CompilerServices;
using LimitOrderBook.Models;

namespace LimitOrderBook.Services;

public class OrderCollection : IDisposable
{
    private int _Capacity;
    private Order[] _Orders;
    private byte[] _AvailableIndices;
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
        _AvailableIndices = new byte[_Capacity];
        Array.Fill<byte>(_AvailableIndices, 1);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int FindOrderIndex(long orderId)
    {
        if (_CountInUse > 0)
        {
            for (int i = 0; i < _Capacity; i++)
            {
                if (_AvailableIndices[i] == 0 && _Orders[i].OrderId == orderId)
                    return i;
            }
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GetOrder(long orderId, ref Order foundOrder)
    {
        int index = FindOrderIndex(orderId);
        if (index == -1)
        {
            foundOrder = ref _Orders[index];
            return true;
        }
        
        return false;
    }

    public int AddOrder(Order order)
    {
        if (_CountInUse < _Capacity)
        {
            int loopCount = 0;
            _LastIndex++;

            while (_AvailableIndices[_LastIndex] == 0 && loopCount < _Capacity)
            {
                _LastIndex = (_LastIndex + 1) & (_Capacity - 1);
                loopCount++;
            }

            if (loopCount < _Capacity)
            {
                _Orders[_LastIndex].InitFrom(order);
                _AvailableIndices[_LastIndex] = 0;
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
                _AvailableIndices[index] = 1;
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