namespace LimitOrderBook.Models;

public enum OrderSide : byte
{
    Unknown = 0,
    Buy = 1,
    Sell = 2
}

public enum OrderType : byte
{
    Unknown = 0,
    Limit = 1,
    Market = 2
    //Stop,
    //TrailingStop
}

public enum OrderStatus : byte
{
    Unknown = 0,
    Resting = 1,
    Filled = 2,
    PartiallyFilled = 3,
    Canceled = 4,
    Duplicate = 5,
    NotFound = 6,
    RejectedGeneral = 16,
    //RejectedReasonA = 17,
    //RejectedReasonB = ...
}

public enum TimeToLive : byte
{
    //GoodForDay = 0,
    GoodTilCanceled = 1
}
