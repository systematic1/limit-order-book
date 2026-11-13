namespace LimitOrderBook.Models;

public struct FillResults
{
    public long OrderID { get; set; }
    public int RequestedQuantity { get; set; }
    public int FilledQuantity { get; set; }
    public int RemainingQuantity { get; set; }
    public long FillPrice { get; set; }
    public long AuxData { get; set; }
    public string? Message { get; set; }
}