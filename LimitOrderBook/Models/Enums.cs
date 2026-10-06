namespace LimitOrderBook.Models;

public enum OrderSide : byte
{
    Buy = 0,
    Sell = 1
}

public enum OrderType : byte
{
    Limit = 0,
    Market = 1
    //Stop,
    //TrailingStop
}

public enum OrderStatus : byte
{
    Unknown = 0,
    Filled = 1,
    PartiallyFilled = 2,
    Canceled = 3,
    Duplicate = 4,
    NotFound = 5,
    Rejected = 16,
    //RejectedReasonA = 17,
    //RejectedReasonB = ...
}

public enum TimeToLive : byte
{
    //GoodForDay = 0,
    GoodTilCanceled = 1
}

