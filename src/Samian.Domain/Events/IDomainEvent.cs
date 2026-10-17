namespace Samian.Domain.Events;

// A domain event is a fact: something that already happened and cannot be undone.
// AggregateId tells you WHICH aggregate changed; OccurredOn is injected by the aggregate from
// IClock, because `DateTime.Now` inside the domain makes the test non-deterministic and the
// timestamp meaningless (a fact is a fact in UTC).
public interface IDomainEvent
{
    Guid AggregateId { get; }

    DateTimeOffset OccurredOn { get; }
}
