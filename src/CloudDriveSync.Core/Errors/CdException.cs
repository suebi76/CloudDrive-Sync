namespace CloudDriveSync.Core.Errors;

/// <summary>
/// An error with a CloudDrive-Sync code (CD-xxxx, the same numbering as CloudDrives). The message is the title of the
/// code; <see cref="Detail"/> holds the technical cause, e.g. the answer of the server.
/// </summary>
public sealed class CdException : Exception
{
    public CdException(string code, string? detail = null, Exception? inner = null)
        : base(ErrorCatalog.Get(code).Title, inner)
    {
        Code = code;
        Detail = detail;
    }

    public string Code { get; }
    public string? Detail { get; }

    /// <summary>The CloudDrive-Sync code of any exception (CD-9000 for unexpected ones).</summary>
    public static string CodeOf(Exception exception) => exception switch
    {
        CdException cd => cd.Code,
        AggregateException { InnerException: { } inner } => CodeOf(inner),
        _ => "CD-9000",
    };
}
