using DssHarness.Core.Execution;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// A run is spelt one way in every line that names one, and the line a command says its own run in is read back to
/// that run and to no other.
/// </summary>
public sealed class RunLineTests
{
    private const string Id = "20261012-150233-1a2b3c4d";

    /// <summary>A run is its id after the word, whether the run is in hand or only its id as a record kept it.</summary>
    [Fact]
    public void ARunIsSpeltAsTheWordAndItsId()
    {
        Assert.Equal($"run {Id}", RunLine.Message(RunId.Parse(Id)));
        Assert.Equal($"run {Id}", RunLine.Message(Id));
    }

    /// <summary>What a command writes of its run, as its output writes any line, is read back to the same run.</summary>
    [Fact]
    public void WhatACommandSaysOfItsRun_IsReadBackToTheSameRun()
    {
        var written = new StringWriter();
        var run = RunId.New();

        new ConsoleHarnessOutput(written, new StringWriter(), verbose: false).Info("test", RunLine.Message(run));

        Assert.True(RunLine.TryRead(written.ToString().TrimEnd('\r', '\n'), "test", out var read));
        Assert.Equal(run, read);
    }

    /// <summary>
    /// A line that only begins like a command's run line is not one: a program a leg starts prints what it likes, and a
    /// line of the command's own that goes on after the word is another sentence.
    /// </summary>
    [Theory]
    [InlineData($"test: run {Id} on wsl Ubuntu")]
    [InlineData($"test: run {Id} ")]
    [InlineData("test: run 20261012-150233")]
    [InlineData("test: run 20261012-150233-1A2B3C4D")]
    [InlineData("test: run")]
    [InlineData("test: running 2 legs")]
    [InlineData($" test: run {Id}")]
    [InlineData($"linux-x86_64-debug: test: run {Id}")]
    [InlineData("")]
    public void ALineThatOnlyBeginsLikeARunLine_IsNotRead(string line)
    {
        Assert.False(RunLine.TryRead(line, "test", out var read));
        Assert.Null(read);
    }

    /// <summary>The line is a command's own: another command's is not read as it.</summary>
    [Fact]
    public void AnotherCommandsRunLine_IsNotRead()
    {
        Assert.True(RunLine.TryRead($"build: run {Id}", "build", out _));
        Assert.False(RunLine.TryRead($"build: run {Id}", "test", out _));
    }

    /// <summary>Who holds something is named with its run as every line spells one, on this machine and on another.</summary>
    [Fact]
    public void AHolderIsNamedWithItsRun_AsALineSpellsOne()
    {
        var since = new DateTimeOffset(2026, 9, 30, 16, 32, 14, TimeSpan.Zero);
        var identity = new ProcessIdentity(new HostPlatform());

        Assert.Equal($"pid 12, {RunLine.Message(Id)}, since 2026-09-30 16:32:14Z", ProcessHolders.Describe(12, Id, since));
        Assert.Equal(
            $"pid 12 on another machine, {RunLine.Message(Id)}, since 2026-09-30 16:32:14Z",
            identity.Describe(identity.CurrentMachine + "-elsewhere", 12, Id, since));
    }
}
