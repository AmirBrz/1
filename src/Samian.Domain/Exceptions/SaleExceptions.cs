namespace Samian.Domain.Exceptions;

public class SaleNotActiveException : DomainException
{
    public SaleNotActiveException(): base("The sale event is not active right now.") { }

}

public class SaleSoldOutException : DomainException
{
    public SaleSoldOutException() : base("The sale event has no units remaining.") { }
}