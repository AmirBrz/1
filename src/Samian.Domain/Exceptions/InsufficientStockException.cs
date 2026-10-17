namespace Samian.Domain.Exceptions;
//have all of them inherit a DomainException : Exception base so your API can map it once in middleware later (Step 7).
// It inherited plain Exception before, so the middleware would have had to catch two unrelated base
// types — exactly the bug that comment was warning about.
public class InsufficientStockException : DomainException
{
    public InsufficientStockException() : base("Insufficient stock for the requested product.") { }

    public InsufficientStockException(string productDescription)
        : base($"Insufficient stock for '{productDescription}'.") { }
}
