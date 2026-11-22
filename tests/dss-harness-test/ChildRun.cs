using System.Reflection;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Tests;

/// <summary>What a static test method run in a child said: how the child exited, and what it wrote on each stream.</summary>
/// <param name="Exit">The child's exit code: 0 where the method returned, 1 where it threw, the system's own code where the child died.</param>
/// <param name="Output">What the method returned, written on the child's output as it is.</param>
/// <param name="Error">What the child wrote on its error stream: the exception the method threw, or the runtime's last words.</param>
internal sealed record ChildRunAnswer(int Exit, string Output, string Error);

/// <summary>
/// Runs a static test method in a child process of this assembly and answers how it ended. A test of a walk over input
/// nested thousands deep runs its work here: a reading that recursed once per level would overflow the child's stack,
/// which no catch stops, and end that child - never the run of the suite - so the test fails on the child's exit.
/// </summary>
/// <remarks>
/// The method takes nothing and returns a string, a <see cref="Task{TResult}"/> of one, a <see cref="Task"/> or nothing;
/// what it returns is the child's output, and what it throws - a failed assertion among them - is the child's error and
/// its exit 1. It runs on the child's main thread, with the stack the system gives that thread.
/// </remarks>
internal static class ChildRun
{
    /// <summary>The child mode that runs a method (<see cref="TestChild"/>).</summary>
    internal const string Mode = "run-method";

    /// <summary>Runs <paramref name="method"/> of <paramref name="type"/> in a child and answers its exit and streams.</summary>
    /// <param name="type">The type that declares the method.</param>
    /// <param name="method">The method's name: static, taking nothing.</param>
    /// <param name="cancellationToken">Stops the child.</param>
    public static async Task<ChildRunAnswer> MethodAsync(Type type, string method, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        var result = await new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create())
            .RunAsync(TestHost.ChildRequest(Mode, type.FullName!, method), cancellationToken)
            .ConfigureAwait(false);

        return new ChildRunAnswer(result.ExitCode, result.StandardOutput.ReplaceLineEndings("\n"), result.StandardError.ReplaceLineEndings("\n"));
    }

    /// <summary>The child's side: runs the method <c>arguments[1]</c> of the type <c>arguments[0]</c> names, writing what it returns.</summary>
    /// <param name="output">The child's output.</param>
    /// <param name="error">The child's error stream.</param>
    /// <param name="arguments">The type's full name, then the method's name.</param>
    internal static int Run(TextWriter output, TextWriter error, string[] arguments)
    {
        var type = typeof(ChildRun).Assembly.GetType(arguments[0], throwOnError: true)!;
        var method = type.GetMethod(arguments[1], BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, [])
            ?? throw new MissingMethodException(type.FullName, arguments[1]);

        try
        {
            var returned = method.Invoke(null, null);
            var said = returned switch
            {
                Task<string> text => text.GetAwaiter().GetResult(),
                Task task => Awaited(task),
                string text => text,
                _ => null,
            };

            if (said is not null)
            {
                output.Write(said);
            }

            return 0;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is { } thrown)
        {
            error.Write(thrown.ToString());
            return 1;
        }
        catch (Exception ex)
        {
            error.Write(ex.ToString());
            return 1;
        }
    }

    /// <summary>Waits for <paramref name="task"/>, which answers nothing.</summary>
    private static string? Awaited(Task task)
    {
        task.GetAwaiter().GetResult();
        return null;
    }
}
