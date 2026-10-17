namespace Samian.Domain.Enums;

// Which document family a number belongs to (the ERP "series"). Order is the only one used by the
// Order aggregate today; the others are here because DocumentNumber.Format is shared by all of them.
public enum DocumentSeries
{
    Order = 1,
    Invoice = 2,
    DeliveryNote = 3,
    CreditNote = 4
}

// static + non-generic is mandatory for extension methods (CS1106 otherwise).
public static class DocumentSeriesExtensions
{
    // The prefix is derived, never stored: one source of truth for "Order -> ORD".
    public static string ToPrefix(this DocumentSeries series) => series switch
    {
        DocumentSeries.Order => "ORD",
        DocumentSeries.Invoice => "INV",
        DocumentSeries.DeliveryNote => "DLV",
        DocumentSeries.CreditNote => "CRN",
        _ => throw new ArgumentOutOfRangeException(nameof(series), series, "Unknown document series.")
    };
}
