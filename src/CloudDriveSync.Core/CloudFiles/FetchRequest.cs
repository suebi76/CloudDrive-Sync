using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using System.Runtime.Versioning;

namespace CloudDriveSync.Core.CloudFiles;

/// <summary>Why data for an online-only file could not be delivered; Windows shows a matching message.</summary>
public enum FetchFailure
{
    /// <summary>Something else went wrong (the version in the cloud changed, the server refused ...).</summary>
    Unsuccessful,
    NetworkUnavailable,
    Cancelled,
    AuthenticationFailed,
    DiskFull,
}

/// <summary>
/// Windows asks for data of an online-only file: a program opened it, or CloudDrive-Sync fetches it on purpose. The
/// answer goes back in pieces (<see cref="Transfer"/>) - each piece resets Windows' clock of 60 seconds, so large
/// files can take as long as they need - or as a failure (<see cref="Fail"/>). Pieces must start at a multiple of
/// 4 KB and be a multiple of 4 KB long, except the last piece of the file.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed class FetchRequest
{
    private readonly CF_CONNECTION_KEY _connection;
    private readonly long _requestKey;

    internal FetchRequest(CF_CONNECTION_KEY connection, long transferKey, long requestKey, string path, byte[] identity, long fileSize, long offset, long length, string? process)
    {
        _connection = connection;
        TransferKey = transferKey;
        _requestKey = requestKey;
        Path = path;
        Identity = identity;
        FileSize = fileSize;
        Offset = offset;
        Length = length;
        Process = process;
    }

    /// <summary>Full path of the file on the PC.</summary>
    public string Path { get; }
    /// <summary>What CloudDrive-Sync stored with the placeholder when it created it.</summary>
    public byte[] Identity { get; }
    /// <summary>The size of the placeholder, i.e. of the version it stands for.</summary>
    public long FileSize { get; }
    /// <summary>The range Windows needs; it may reach beyond the end of the file (only up to the end is sent).</summary>
    public long Offset { get; }
    public long Length { get; }
    /// <summary>Identifies the request; a cancellation names the same key.</summary>
    public long TransferKey { get; }
    /// <summary>File name of the program that opened the file, when Windows tells it.</summary>
    public string? Process { get; }

    /// <summary>The end of the range that has to be delivered: the requested range, but never beyond the file.</summary>
    public long End => Math.Min(Offset + Length, FileSize);

    /// <summary>Hands one piece of the file to Windows.</summary>
    public void Transfer(ReadOnlySpan<byte> data, long offset) => Execute(data, offset, data.Length, NTSTATUS.STATUS_SUCCESS);

    /// <summary>Tells Windows that the range from <paramref name="offset"/> on cannot be delivered; the program gets an error.</summary>
    public void Fail(FetchFailure failure, long offset)
    {
        var start = offset - offset % 4096;
        var status = failure switch
        {
            FetchFailure.NetworkUnavailable => NTSTATUS.STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE,
            FetchFailure.Cancelled => NTSTATUS.STATUS_CLOUD_FILE_REQUEST_CANCELED,
            FetchFailure.AuthenticationFailed => NTSTATUS.STATUS_CLOUD_FILE_AUTHENTICATION_FAILED,
            FetchFailure.DiskFull => NTSTATUS.STATUS_CLOUD_FILE_INSUFFICIENT_RESOURCES,
            _ => NTSTATUS.STATUS_CLOUD_FILE_UNSUCCESSFUL,
        };
        Execute([], start, Offset + Length - start, status);
    }

    private unsafe void Execute(ReadOnlySpan<byte> data, long offset, long length, NTSTATUS status)
    {
        var operation = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = _connection,
            TransferKey = TransferKey,
            RequestKey = _requestKey,
        };
        var parameters = new CF_OPERATION_PARAMETERS();
        // The size of the parameters for this operation only: the header plus the TransferData part of the union.
        parameters.ParamSize = (uint)((byte*)&parameters.Anonymous - (byte*)&parameters) + (uint)sizeof(CF_OPERATION_PARAMETERS._Anonymous_e__Union._TransferData_e__Struct);
        parameters.TransferData.CompletionStatus = status;
        parameters.TransferData.Offset = offset;
        parameters.TransferData.Length = length;
        fixed (byte* pointer = data)
        {
            parameters.TransferData.Buffer = pointer;
            Placeholders.Check(PInvoke.CfExecute(&operation, &parameters), "CfExecute(TRANSFER_DATA)", Path);
        }
    }
}
