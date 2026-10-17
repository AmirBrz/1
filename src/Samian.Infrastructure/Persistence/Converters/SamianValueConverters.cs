using System.Globalization;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Samian.Domain.ValueObjects;

namespace Samian.Infrastructure.Persistence.Converters;

// HOW a value object becomes a column is an INFRASTRUCTURE decision, which is why it is not written
// anywhere in the Domain. The rule chosen here: Money maps to ONE text column "amount|currency"
// instead of two columns.
//
// The trade-off, stated honestly because it will be asked:
//   + the whole value object stays atomic (a row can never be 500 in one currency and USD in another),
//   + no owned-type gymnastics: Money is a readonly struct with public FIELDS, and EF Core maps
//     properties, so an owned type (Amount + Currency columns) is not available without changing the VO;
//   - a query like "orders with Total > 1.000.000" cannot use that column (text sort != numeric sort).
//     The fix when reporting needs it is a read model / query side, not two extra columns on the
//     aggregate (writing derived columns into the aggregate is how they drift out of sync).
public static class SamianValueConverters
{
    // Member names are suffixed WithConverterPurpose on purpose: a static field named `Money` would hide
    // the Money TYPE inside this class body (CS0118), and the parse helpers below need that type.
    public static readonly ValueConverter<Money, string> MoneyToString = new(
        money => ToMoneyColumn(money),
        column => FromMoneyColumn(column));

    // The provider-side string is parsed by the value object itself, so a corrupted row fails as a
    // DomainException on materialization instead of silently producing a nonsense price.
    private static string ToMoneyColumn(Money money) =>
        string.Create(CultureInfo.InvariantCulture, $"{money.Amount}|{money.Currency}");

    private static Money FromMoneyColumn(string column)
    {
        var separatorIndex = column.IndexOf('|');

        if (separatorIndex <= 0 || separatorIndex == column.Length - 1)
            throw new InvalidOperationException($"Stored money value '{column}' is not in the expected 'amount|currency' form.");

        var amount = decimal.Parse(column.AsSpan(0, separatorIndex), CultureInfo.InvariantCulture);
        var currency = column[(separatorIndex + 1)..];

        return new Money(amount, currency);
    }

    // DocumentNumber -> "ORD-000123": one column, and the value object's own regex validates on read.
    public static readonly ValueConverter<DocumentNumber, string> DocumentNumberToString = new(
        number => number.Value,
        column => DocumentNumber.Parse(column));

    // A ValueComparer is required for converted REFERENCE types: EF's default comparer does
    // reference equality, so a re-assigned identical number would be tracked as a change and
    // equality in a Where() has nothing to compare provider values with. Snapshot is identity because
    // DocumentNumber is immutable — a snapshot function that returned a mutated copy would be the
    // classic EF concurrency hole. (ValueComparer is abstract; ValueComparer<T> is the concrete type.)
    public static readonly ValueComparer<DocumentNumber> DocumentNumberComparer = new(
        (left, right) => Equals(left, right),
        // `== null` rather than `is null`: a ValueComparer receives EXPRESSION trees (EF translates them
        // into provider-side comparisons), and an `is` pattern is not an allowed node in one.
        value => value == null ? 0 : value.GetHashCode(),
        value => value);
    
    // Same reasoning for the struct: Money's own value-based Equals is what EF must use, and the default
    // object comparer would box and compare fields rather than call it.
    public static readonly ValueComparer<Money> MoneyComparer = new(
        (left, right) => left.Equals(right),
        value => value.GetHashCode(),
        value => value);
}
