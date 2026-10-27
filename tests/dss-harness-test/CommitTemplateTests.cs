using DssHarness.Core.Configuration;

namespace DssHarness.Tests;

/// <summary>
/// The one owner of <c>commit.template</c>'s grammar and its filling: the placeholders the configuration's check reads,
/// and the message a commit step commits with - the step's own where no template is declared, the template filled in one
/// pass where one is - refused where it would be none, blank, or leave a required variable or an undeclared one.
/// </summary>
public sealed class CommitTemplateTests
{
    private static readonly CommitConfig Template = new()
    {
        Template = "{type}({Scope}): {summary}\n\n{body}",
        Variables = new(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = new CommitVariable { Required = true },
            ["scope"] = new CommitVariable { Default = "core" },
            ["summary"] = new CommitVariable { Required = true, Description = "what the change does, in a line" },
            ["body"] = new CommitVariable(),
        },
    };

    /// <summary>The placeholders are each name once, whatever its case, in the order first named.</summary>
    [Fact]
    public void ThePlaceholders_AreEachNameOnce_WhateverItsCase()
        => Assert.Equal(["type", "Scope", "summary", "body"], CommitTemplate.Placeholders(Template.Template! + " {TYPE} {scope}"));

    /// <summary>
    /// With a template, each placeholder is the step's value for it - its name matched whatever its case - else its
    /// default, else nothing; filled once, so a value that holds braces is written as it is.
    /// </summary>
    [Fact]
    public void WithATemplate_EachPlaceholderIsTheStepsValue_ElseItsDefault_FilledOnce()
    {
        var message = CommitTemplate.Message(
            Template,
            message: "not used",
            new Dictionary<string, string> { ["TYPE"] = "fix", ["summary"] = "keep {type} as written" },
            out var problem);

        Assert.Null(problem);
        Assert.Equal("fix(core): keep {type} as written\n\n", message);
    }

    /// <summary>
    /// Without a template the step's own message is the message; none, or a blank one, is refused, as is a template that
    /// leaves a required variable without a value or uses one the configuration does not declare.
    /// </summary>
    [Fact]
    public void AMessageThatWouldBeNoneBlankOrHalfFilled_IsRefused()
    {
        var none = new CommitConfig();

        Assert.Equal("checkpoint", CommitTemplate.Message(none, "checkpoint", new Dictionary<string, string>(), out _));
        Assert.Null(CommitTemplate.Message(none, null, new Dictionary<string, string>(), out var missing));
        Assert.Equal(CommitTemplate.NoMessage, missing);
        Assert.Equal("names no message and the configuration declares no commit.template; one of the two writes the commit's message", missing);
        Assert.Null(CommitTemplate.Message(none, " \n", new Dictionary<string, string>(), out var blank));
        Assert.Equal("has a message that is blank once filled, and git refuses to commit a blank message: give the step a message, or values that fill the template", blank);

        // What the variable means is said beside it, where the configuration says.
        Assert.Null(CommitTemplate.Message(Template, null, new Dictionary<string, string> { ["type"] = "fix" }, out var required));
        Assert.Equal("gives no value for 'summary', which commit.template requires (what the change does, in a line): add it under the step's variables", required);
        Assert.Equal("gives no value for 'type', which commit.template requires: add it under the step's variables", CommitTemplate.NoValue("type", Template.Variables["type"]));

        var undeclared = new CommitConfig { Template = "{type} {kind}", Variables = new(StringComparer.OrdinalIgnoreCase) { ["type"] = new CommitVariable() } };

        Assert.Null(CommitTemplate.Message(undeclared, null, new Dictionary<string, string> { ["type"] = "fix" }, out var unknown));
        Assert.Equal("cannot fill its message: commit.template uses {kind}, which is not declared in commit.variables; declare it there, or take it out of the template", unknown);
        Assert.Equal("commit.template uses {kind}, which is not declared in commit.variables; declare it there, or take it out of the template", CommitTemplate.Undeclared("kind"));

        var empty = new CommitConfig { Template = "{body}", Variables = new(StringComparer.OrdinalIgnoreCase) { ["body"] = new CommitVariable() } };

        Assert.Null(CommitTemplate.Message(empty, null, new Dictionary<string, string>(), out var filledBlank));
        Assert.Equal("has a message that is blank once filled, and git refuses to commit a blank message: give the step a message, or values that fill the template", filledBlank);
    }

    /// <summary>
    /// The template is read by the configuration's one brace grammar: a doubled brace is a brace, <c>${...}</c> is left as
    /// written, and a placeholder never closed or naming nothing is refused - by the configuration's check and by the
    /// filling alike.
    /// </summary>
    [Fact]
    public void TheTemplate_IsReadByTheOneBraceGrammar()
    {
        var variables = new Dictionary<string, CommitVariable>(StringComparer.OrdinalIgnoreCase) { ["type"] = new CommitVariable() };
        var commit = new CommitConfig { Template = "{{literal}} ${HOME} {type}", Variables = variables };

        Assert.Equal(["type"], CommitTemplate.Placeholders(commit.Template));
        Assert.Null(CommitTemplate.GrammarProblem(commit.Template));
        Assert.Equal("{literal} ${HOME} fix", CommitTemplate.Message(commit, null, new Dictionary<string, string> { ["type"] = "fix" }, out _));
        Assert.Equal("commit.template is not written whole: the placeholder at column 6 of line 1 is never closed", CommitTemplate.GrammarProblem("fix: {type"));
        Assert.Equal(
            "commit.template is not written whole: the placeholder at column 1 of line 1 names nothing (a brace meant literally is written twice)",
            CommitTemplate.GrammarProblem("{} fix"));

        var broken = new CommitConfig { Template = "fix: {type", Variables = variables };

        Assert.Null(CommitTemplate.Message(broken, null, new Dictionary<string, string> { ["type"] = "fix" }, out var problem));
        Assert.Equal("cannot fill its message: commit.template is not written whole: the placeholder at column 6 of line 1 is never closed", problem);
    }
}
