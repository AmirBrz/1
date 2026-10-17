namespace Samian.Domain.Exceptions;

// Invariant violations of the model. Nothing in Application/Infrastructure needs to know which
// specific rule broke — catching this one base type in middleware is enough (that's Step 7).
public class DomainException : Exception
{
    public DomainException() { }

    public DomainException(string message) : base(message) { }

    public DomainException(string message, Exception innerException) : base(message, innerException) { }
}
