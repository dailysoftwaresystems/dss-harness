using System.Runtime.InteropServices;

namespace DssHarness.Core.Execution;

/// <summary>
/// The calls, made to the C library this process runs on: found by name - <c>libc.so.6</c>, then <c>libc.so</c>,
/// then <c>libc</c> - and bound once. The functions and their record are the same in glibc and musl.
/// </summary>
internal sealed class LibcInotifyCalls : IInotifyCalls
{
    /// <summary>The names the C library is looked for under, in this order.</summary>
    public static readonly IReadOnlyList<string> Libraries = ["libc.so.6", "libc.so", "libc"];

    private readonly InitFunction _init;
    private readonly AddWatchFunction _addWatch;
    private readonly RemoveWatchFunction _removeWatch;
    private readonly ReadFunction _read;
    private readonly CloseFunction _close;

    private LibcInotifyCalls(IntPtr library)
    {
        _init = Bound<InitFunction>(library, "inotify_init1");
        _addWatch = Bound<AddWatchFunction>(library, "inotify_add_watch");
        _removeWatch = Bound<RemoveWatchFunction>(library, "inotify_rm_watch");
        _read = Bound<ReadFunction>(library, "read");
        _close = Bound<CloseFunction>(library, "close");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    private delegate int InitFunction(int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    private delegate int AddWatchFunction(int instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mask);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    private delegate int RemoveWatchFunction(int instance, int watch);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    private delegate nint ReadFunction(int instance, [Out] byte[] buffer, nuint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
    private delegate int CloseFunction(int instance);

    /// <summary>
    /// The calls, bound; or <see langword="null"/> and why, where no C library loads that has every one of them - a
    /// machine whose C library is none of these names, say.
    /// </summary>
    /// <param name="why">Why they could not be bound, in a few words: what a warning says.</param>
    public static LibcInotifyCalls? TryBind(out string? why)
    {
        foreach (var name in Libraries)
        {
            if (!NativeLibrary.TryLoad(name, out var library))
            {
                continue;
            }

            var missing = Functions.FirstOrDefault(function => !NativeLibrary.TryGetExport(library, function, out _));

            if (missing is null)
            {
                why = null;
                return new LibcInotifyCalls(library);
            }

            why = $"'{name}' has no {missing}";
            return null;
        }

        why = $"no C library loads as {string.Join(", ", Libraries.SkipLast(1))} or {Libraries[^1]}";
        return null;
    }

    /// <inheritdoc/>
    public int Init(int flags, out int error) => Answered(_init(flags), out error);

    /// <inheritdoc/>
    public int AddWatch(int instance, string path, uint mask, out int error) => Answered(_addWatch(instance, path, mask), out error);

    /// <inheritdoc/>
    public int RemoveWatch(int instance, int watch, out int error) => Answered(_removeWatch(instance, watch), out error);

    /// <inheritdoc/>
    public int Read(int instance, byte[] buffer, out int error) => Answered((int)_read(instance, buffer, (nuint)buffer.Length), out error);

    /// <inheritdoc/>
    public void Close(int instance) => _close(instance);

    /// <summary>The functions bound, each by the name the C library exports it under.</summary>
    private static IEnumerable<string> Functions => ["inotify_init1", "inotify_add_watch", "inotify_rm_watch", "read", "close"];

    private static TFunction Bound<TFunction>(IntPtr library, string function)
        where TFunction : Delegate
        => Marshal.GetDelegateForFunctionPointer<TFunction>(NativeLibrary.GetExport(library, function));

    private static int Answered(int result, out int error)
    {
        error = result < 0 ? Marshal.GetLastPInvokeError() : 0;
        return result;
    }
}
