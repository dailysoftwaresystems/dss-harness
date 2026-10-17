namespace DssHarness.Core.Application;

/// <summary>
/// The one thing a front end may resolve: a use case of this tool, callable from the command line today and from a
/// graphical front end later, with no knowledge of how either of them is built.
/// </summary>
/// <remarks>
/// A marker, deliberately empty: what it marks is the seam - everything behind one of these is this tool's behaviour,
/// and everything a front end reaches of Core it reaches through one of these, its output, or its results.
/// </remarks>
public interface IUseCase
{
}
