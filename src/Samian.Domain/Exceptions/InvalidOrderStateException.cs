using Samian.Domain.Enums;

namespace Samian.Domain.Exceptions;

// Thrown by the Order state machine. Carrying from/to makes the message exact ("Placed -> Shipped")
// instead of the vague "cannot be shipped if it is not in confirmed status" the entity used to repeat
// by hand in five places — one type, one message, no drift between code and wording.
public class InvalidOrderStateException : DomainException
{
    public InvalidOrderStateException(OrderStatus from, OrderStatus to)
        : base($"An order cannot move from '{from}' to '{to}'.")
    {
        From = from;
        To = to;
    }

    public OrderStatus From { get; }

    public OrderStatus To { get; }
}
