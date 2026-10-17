namespace Samian.Domain.Common;

// A value object has no identity: two "ORD-000123" numbers are THE SAME thing, not two copies.
// So equality compares all components, and the type is immutable after construction.
//
// Why this old version could not be used at all: it was `sealed class ValueObject<T>`, which means
// (a) nothing could inherit it, and (b) `protected EqualsCore` inside a sealed type is dead code.
public abstract class ValueObject
{
    // Each value object declares its own components — explicit, and no reflection per comparison.
    protected abstract IEnumerable<object?> GetEqualityComponents();

    public override bool Equals(object? obj)
    {
        if (obj is null || obj.GetType() != GetType())
            return false;

        return GetEqualityComponents()
            .SequenceEqual(((ValueObject)obj).GetEqualityComponents());
    }

    public override int GetHashCode()
    {
        var hashCode = new HashCode();
        foreach (var component in GetEqualityComponents())
            hashCode.Add(component);

        return hashCode.ToHashCode();
    }

    public static bool operator ==(ValueObject? left, ValueObject? right) => Equals(left, right);

    public static bool operator !=(ValueObject? left, ValueObject? right) => !Equals(left, right);

    // The reflection-based variant you had before is kept out of the hot path on purpose: it is fine
    // for a handful of comparisons, but Money/DocumentNumber compare on a typed list instead.
}
