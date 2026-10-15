using System.Runtime.InteropServices;

namespace DssHarness.Core.Execution;

/// <summary>
/// The calls an inotify instance is made, watched and read through. Each answers as the C library's own does - the
/// result, or -1 with the error number - so a test can say what a machine out of instances or watches says, and no
/// test need take either from the machine it runs on.
/// </summary>
internal interface IInotifyCalls
{
    /// <summary><c>inotify_init1</c>: a new instance's descriptor, or -1.</summary>
    /// <param name="flags">Its flags: <see cref="InotifyBits.CloseOnExec"/>.</param>
    /// <param name="error">Why it failed, where it did.</param>
    int Init(int flags, out int error);

    /// <summary><c>inotify_add_watch</c>: the watch's descriptor - the one the instance already has for the directory, where it has one - or -1.</summary>
    /// <param name="instance">The instance.</param>
    /// <param name="path">The directory.</param>
    /// <param name="mask">What to be told of.</param>
    /// <param name="error">Why it failed, where it did.</param>
    int AddWatch(int instance, string path, uint mask, out int error);

    /// <summary><c>inotify_rm_watch</c>: 0, or -1.</summary>
    /// <param name="instance">The instance.</param>
    /// <param name="watch">The watch's descriptor.</param>
    /// <param name="error">Why it failed, where it did.</param>
    int RemoveWatch(int instance, int watch, out int error);

    /// <summary><c>read</c>: how many bytes of records were read into <paramref name="buffer"/>, blocking until there are some; or -1.</summary>
    /// <param name="instance">The instance.</param>
    /// <param name="buffer">Where they go.</param>
    /// <param name="error">Why it failed, where it did.</param>
    int Read(int instance, byte[] buffer, out int error);

    /// <summary><c>close</c>: the instance let go, with every watch it held.</summary>
    /// <param name="instance">The instance.</param>
    void Close(int instance);
}

/// <summary>The numbers of the errors the calls answer that are told apart: the same on every Linux this tool runs on.</summary>
internal static class InotifyErrors
{
    /// <summary><c>EINTR</c>: a signal came first; the call is made again.</summary>
    public const int Interrupted = 4;

    /// <summary><c>EMFILE</c>: at <c>inotify_init1</c>, this user's instances are all taken.</summary>
    public const int InstancesTaken = 24;

    /// <summary><c>ENOSPC</c>: at <c>inotify_add_watch</c>, this user's watches ran out.</summary>
    public const int WatchesRanOut = 28;

    /// <summary>The system's own words for <paramref name="error"/>.</summary>
    /// <param name="error">An error number a call answered.</param>
    public static string Said(int error) => Marshal.GetPInvokeErrorMessage(error);
}
