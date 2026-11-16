using AgentMesh.Core.Models;
using FluentAssertions;

namespace AgentMesh.Tests.Models;

/// <summary>
/// Every wire shape a question has actually arrived in, read by the one parser that knows them.
/// </summary>
/// <remarks>
/// <para>
/// Two readers of this payload used to disagree: the session surface unwrapped an array that arrived as a
/// string, and the Stage 1 activity did not. The job rendered both questions in the session chat, the
/// operator answered both, and the workflow recorded that nothing had been asked. That is why the shapes
/// live here rather than in whichever caller noticed them: a reader that only accepts one shape does not
/// fail loudly, it fails as a confident "no questions".
/// </para>
/// <para>
/// The encoded-array case is quoted from a live Antigravity transcript (job c28eca57, 2026-11-16), where
/// the driver serialises its <c>questions</c> parameter as a string and the runner forwards it verbatim.
/// </para>
/// </remarks>
public class AskQuestionPayloadTests
{
    /// <summary>The verbatim <c>session_messages.tool_args</c> value that job c28eca57 parked on.</summary>
    private const string AntigravityEncodedForm =
        """
        {"questions": "[{\"is_multi_select\":false,\"options\":[\"(Recommended) Re-provision the workspace\",\"Clone budget-app into /workspace/repos/budget-app\"],\"question\":\"How should the repository workspace be resolved?\"},{\"is_multi_select\":false,\"options\":[\"All 10 issues\",\"Strictly listed files only\"],\"question\":\"What scope should this job deliver?\"}]"}
        """;

    [Fact]
    public void ArrayForm_IsRead()
    {
        var parsed = AskQuestionPayload.Parse(
            """{"questions":[{"question":"Which queue?","options":["Temporal","Redis"]}]}""");

        parsed.Should().ContainSingle();
        parsed[0].Question.Should().Be("Which queue?");
        parsed[0].Options.Should().Equal("Temporal", "Redis");
    }

    [Fact]
    public void EncodedArrayForm_IsRead_AndKeepsItsOptionsInOrder()
    {
        var parsed = AskQuestionPayload.Parse(AntigravityEncodedForm);

        parsed.Should().HaveCount(2, "both questions were really asked, and losing the second is the bug");
        parsed[0].Question.Should().Be("How should the repository workspace be resolved?");
        parsed[0].Options.Should().ContainInOrder(
            "(Recommended) Re-provision the workspace",
            "Clone budget-app into /workspace/repos/budget-app");
        parsed[1].Options.Should().HaveCount(2);
        parsed[0].IsMultiSelect.Should().BeFalse();
    }

    [Fact]
    public void BareArray_IsRead()
    {
        var parsed = AskQuestionPayload.Parse("""[{"question":"Which queue?"},{"question":"Which cache?"}]""");

        parsed.Select(q => q.Question).Should().Equal("Which queue?", "Which cache?");
    }

    [Fact]
    public void SingleQuestionPerCall_IsReadAsTheOneItemListItDescribes()
    {
        var parsed = AskQuestionPayload.Parse(
            """{"question":"Ship it?","options":["Yes","No"],"is_multi_select":false}""");

        parsed.Should().ContainSingle().Which.Options.Should().Equal("Yes", "No");
    }

    [Fact]
    public void MultiSelectFlag_IsReadFromEitherSpelling()
    {
        AskQuestionPayload.Parse("""{"questions":[{"question":"Which flags?","is_multi_select":true}]}""")
            .Single().IsMultiSelect.Should().BeTrue();

        AskQuestionPayload.Parse("""{"questions":[{"question":"Which flags?","isMultiSelect":"true"}]}""")
            .Single().IsMultiSelect.Should().BeTrue("a driver that stringifies booleans still means multi-select");
    }

    [Fact]
    public void OptionObjects_AreReadByTheirLabel()
    {
        var parsed = AskQuestionPayload.Parse(
            """{"questions":[{"question":"Which model?","options":[{"label":"Sol","value":"gpt-6-sol"},{"text":"Luna"}]}]}""");

        parsed.Single().Options.Should().Equal("Sol", "Luna");
    }

    [Fact]
    public void PascalCaseKeys_AreRead()
    {
        AskQuestionPayload.Parse("""{"Questions":[{"Question":"Which queue?"}]}""")
            .Single().Question.Should().Be("Which queue?");
    }

    [Fact]
    public void QuestionsWithNoText_AreDroppedRatherThanShownAsBlank()
    {
        var parsed = AskQuestionPayload.Parse(
            """{"questions":[{"options":["A","B"]},{"question":"  "},{"id":"scope","options":["A"]},{"question":"Which scope?"}]}""");

        parsed.Select(q => q.Question).Should().ContainInOrder("scope", "Which scope?")
            .And.HaveCount(2,
            "an entry with only an id still names a question, an entry with neither names nothing");
    }

    [Fact]
    public void ProseYieldsNothingBecauseProseIsNotAQuestionChannel()
    {
        AskQuestionPayload.Parse("Q1. Should we use Postgres? Q2. Do we need compatibility?")
            .Should().BeEmpty("a reader that guesses over prose turns thinking-out-loud into an operator question");
    }

    [Fact]
    public void QuestionsBeyondTheCap_AreDroppedNotPaginated()
    {
        var six = string.Join(",", Enumerable.Range(1, 6).Select(i => $$"""{"question":"Question {{i}}?"}"""));

        AskQuestionPayload.Parse($$"""{"questions":[{{six}}]}""")
            .Should().HaveCount(AskQuestionPayload.MaxQuestions,
                "past the cap the operator is filling a form instead of steering a job");
    }
}
