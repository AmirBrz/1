namespace Samian.Domain.Common;

public interface IEntity<TId>
{
    TId Id { get; }
}

// "abstract" because no business concept IS a bare Entity — everything in the domain has a name.
// `where TId : notnull`: an identity type is never nullable, which also lets Equals/GetHashCode
// dereference Id without a null warning.
public abstract class Entity<TId> : IEntity<TId> 
    where TId : notnull
{
    // protected set: only the entity itself (or a subclass) decides when identity is written.
    // A caller must never be able to do order.Id = something else.
    public virtual TId Id { get; protected set; } = default!;

    // Two rules that are easy to forget and expensive when you do:
    // 1) A TRANSIENT entity (Id still = default/Guid.Empty) has no identity yet, so it equals only itself.
    //    Without this, two freshly new()'d orders would compare equal and a HashSet would silently
    //    swallow one of them — the classic EF "duplicate key" bug.
    // 2) Different types are never equal: a Customer and a Product both keyed Guid could collide.
    public override bool Equals(object? obj)
    {
        if (obj is not Entity<TId> other)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        if (GetType() != other.GetType())
            return false;

        if (IsTransient(Id) || IsTransient(other.Id))
            return false;

        return Id.Equals(other.Id);
    }

    // Must agree with Equals: equal objects need equal hashes. A transient entity hashes by reference
    // (and Id.GetHashCode() would NRE when TId is a nullable/reference type).
    public override int GetHashCode()
    {
        if (IsTransient(Id))
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

        return Id.GetHashCode();
    }

    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) => Equals(left, right);

    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !Equals(left, right);

    private static bool IsTransient(TId id) => EqualityComparer<TId>.Default.Equals(id, default!);
}
