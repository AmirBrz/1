namespace Samian.Application.Common;

// Application-level failures are NOT domain failures, and the API has to answer them with different
// status codes, so they are different types (the middleware maps each one in exactly one place):
//
//   DomainException (Domain)              -> 400, an aggregate refused to exist
//   AppValidationException (here)         -> 400, the request was malformed before any rule ran
//   ResourceNotFoundException             -> 404, the id does not exist
//   InvalidCredentialsException           -> 401, authentication failed
//   ForbiddenActionException              -> 403, authenticated but not allowed
//   BusinessConflictException             -> 409, the state or the stock says no
//
// Deliberately not deriving from ASP.NET exceptions: Application must compile without a web reference.
public class AppValidationException : Exception
{
    public AppValidationException(string message) : base(message) { }

    public AppValidationException(IReadOnlyCollection<string> errors)
        : base(string.Join(" ", errors))
    {
        Errors = errors;
    }

    public IReadOnlyCollection<string> Errors { get; } = Array.Empty<string>();
}

public class ResourceNotFoundException : Exception
{
    public ResourceNotFoundException(string what, object key) : base($"{what} '{key}' was not found.") { }
}

public class InvalidCredentialsException : Exception
{
    // One message for "no such user" and "wrong password" on purpose: distinguishing them lets an
    // attacker enumerate valid user names.
    public InvalidCredentialsException()
        : base("User name or password is not correct.") { }
}

public class ForbiddenActionException : Exception
{
    public ForbiddenActionException(string what) : base($"You are not allowed to {what}.") { }
}

public class BusinessConflictException : Exception
{
    public BusinessConflictException(string message) : base(message) { }
}
