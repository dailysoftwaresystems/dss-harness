namespace DssHarness.Core.FileSystem;

/// <summary>
/// Whether a refusal of a file is another open's hold on it - a cause that passes once that open lets go, so the act it
/// refused is tried again for as long as the hold lasts - rather than anything a retry would meet again.
/// </summary>
/// <remarks>
/// The one owner of the question: on Windows a sharing or a lock violation, the system refusing an open, a read or a
/// deletion that another handle's sharing forbids (<see cref="WindowsHolds.HeldBy"/>); on Linux and macOS the exclusive lock
/// .NET asks of an open sharing nothing, refused for another open's (EWOULDBLOCK), which .NET raises with the system's own
/// error number. A read-only file system, a disk's failure, a right this user lacks, any other refusal is no hold.
/// </remarks>
public static class FileHolds
{
    /// <summary>Whether <paramref name="refusal"/> says another open holds the file, and nothing else.</summary>
    /// <param name="refusal">What the act on the file raised.</param>
    public static bool HeldElsewhere(IOException refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        return OperatingSystem.IsWindows() ? WindowsHolds.HeldBy(refusal) : UnixFiles.WouldBlock(refusal.HResult);
    }
}
