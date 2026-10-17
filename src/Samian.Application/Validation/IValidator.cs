using Samian.Application.Common;

namespace Samian.Application.Validation;

// STRATEGY, one rule-set per request type. `in T` (contravariant) lets a validator written for a base
// request be handed to a derived one, and lets a use case depend on IValidator<RegisterRequest> while
// DI swaps in a stricter rule-set with no code change.
//
// Why not FluentValidation (the usual package): it is not in the offline NuGet cache, and hand-rolling
// this is the better interview answer — you can point at the interface and say exactly what a validator
// is, instead of reciting a library's fluent API.
public interface IValidator<in T>
{
    // Returns messages instead of throwing, so a caller can report ALL problems at once. The throw
    // happens in EnsureValid(), which keeps "collect the errors" separate from "reject the request".
    IReadOnlyCollection<string> Validate(T target);
}

// A tiny collector so a validator reads as a list of rules rather than a tree of if-statements.
public sealed class ValidationMessages
{
    private readonly List<string> _errors = new();

    // AddIf(condition-that-is-a-problem, message): the condition is written from the caller's point of
    // view ("the name is too short"), which keeps every rule a positive sentence about the fix.
    public ValidationMessages AddIf(bool isProblem, string message)
    {
        if (isProblem)
            _errors.Add(message);

        return this;
    }

    public ValidationMessages AddIfNot(bool isSatisfied, string message) => AddIf(!isSatisfied, message);

    public IReadOnlyCollection<string> ToList() => _errors;
}

public static class ValidatorExtensions
{
    // The single place a validation failure becomes an exception (and therefore a 400 with all the
    // messages joined). Use cases call this as their first statement — validate, then load, then act.
    public static void EnsureValid<T>(this IValidator<T> validator, T target)
    {
        var errors = validator.Validate(target);

        if (errors.Count > 0)
            throw new AppValidationException(errors);
    }
}
