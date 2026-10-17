using System.Text.RegularExpressions;
using Samian.Domain.Common;
using Samian.Domain.Enums;
using Samian.Domain.Exceptions;

namespace Samian.Domain.ValueObjects;

// DocumentNumber = <series prefix>-<zero padded sequence>, e.g. "ORD-000123".
// Why a value object and not a string on the entity: the format rule lives in ONE place, an
// invalid number can never exist in the model, and equality means "same document" instead of
// "same reference". This is the class-based flavour of ValueObject (no arithmetic, allocated once).
// partial is required because [GeneratedRegex] is a partial method: the SDK source generator writes
// the matching implementation part at build time (it is built into .NET 8, no package, no internet).
public sealed partial class DocumentNumber : ValueObject
{
    public const int SequenceDigits = 6;

    // Generated at compile time by the regex source generator: no interpretation cost per match,
    // and the pattern is validated when you build instead of when a customer hits it.
    [GeneratedRegex(@"^(?<prefix>[A-Z]{3})-(?<sequence>\d{6})$")]
    private static partial Regex FormatRegex();

    public string Value { get; }

    public DocumentSeries Series { get; }

    public int Sequence { get; }

    private DocumentNumber(DocumentSeries series, int sequence)
    {
        Series = series;
        Sequence = sequence;
        Value = $"{series.ToPrefix()}-{sequence.ToString().PadLeft(SequenceDigits, '0')}";
    }

    // Factory is the only way in: the sequence comes from IDocumentNumberGenerator (Infrastructure owns
    // the counter), the format is validated here, so a malformed number cannot be constructed.
    //
    // Decision recorded: NO year in the number (ORD-000123, not ORD-1405-000123). Per-year sequences
    // would add a second counter dimension for no business benefit here.
    public static DocumentNumber From(DocumentSeries series, int sequence)
    {
        Guard.AgainstNonPositive(sequence, () => new DomainException("Document sequence must be greater than zero."));

        // PadLeft does not truncate: sequence 1_000_000 would render 7 digits, and this value object
        // would then produce a string its own regex cannot parse — Parse(From(...).Value) must always
        // round-trip, so the capacity is enforced at construction, not discovered at read time.
        Guard.AgainstInvalidState(sequence < PowerOfTen(SequenceDigits),
            () => new DomainException($"Document sequence must fit in {SequenceDigits} digits."));

        return new DocumentNumber(series, sequence);
    }

    private static int PowerOfTen(int digits)
    {
        var result = 1;
        for (var i = 0; i < digits; i++)
            result *= 10;

        return result;
    }

    // Reading a value that came from the database/API: parse-or-fail, never silently accept.
    public static DocumentNumber Parse(string? value)
    {
        Guard.AgainstEmpty(value, () => new DomainException("Document number is required."));

        var match = FormatRegex().Match(value!);
        if (!match.Success)
            throw new DomainException($"'{value}' is not a valid document number (expected ABC-000001).");

        var sequence = int.Parse(match.Groups["sequence"].Value);
        var prefix = match.Groups["prefix"].Value;

        // FirstOrDefault on an enum returns 0 when nothing matches, and 0 is NOT a defined
        // DocumentSeries — the old code then called series.ToPrefix() inside the guard and threw
        // ArgumentOutOfRangeException instead of the friendly DomainException it was trying to raise.
        // Searching for a nullable keeps "no match" representable.
        var series = Enum.GetValues<DocumentSeries>().Cast<DocumentSeries?>()
            .FirstOrDefault(s => s!.Value.ToPrefix() == prefix);

        Guard.AgainstNull(series, () => new DomainException($"'{value}' has an unknown document series."));

        return new DocumentNumber(series!.Value, sequence);
    }

    public bool IsSameSeries(DocumentNumber other) => Series == other.Series;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value;
}
