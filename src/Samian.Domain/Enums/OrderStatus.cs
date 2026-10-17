namespace Samian.Domain.Enums;

// Values are stored in the database as numbers, so the explicit numbering is a contract:
// never renumber an existing member, only append new ones.
public enum OrderStatus
{
    Draft = 1,    
    Placed = 3,
    Confirmed = 4,
    Shipped = 5,
    Delivered = 6,
    Cancelled = 10
}
