using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>A question from Claude (AskUserQuestion) reaches the phone as readable text, not as a JSON dump, and
/// "Got it" leaves it to the terminal instead of answering for it.</summary>
public sealed class QuestionPromptTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void TheQuestionAndItsChoicesAreListedAsText()
    {
        var text = ClaudeHook.QuestionDetail(Json("""
            {"questions":[
              {"header":"Framework","question":"Which framework?","multiSelect":false,
               "options":[{"label":"React","description":"Component library"},{"label":"Vue","description":""}]},
              {"header":"Extras","question":"Which extras?","multiSelect":true,"options":[{"label":"Tests","description":"xUnit"}]}]}
            """));
        Assert.Equal("Framework: Which framework?\n  • React — Component library\n  • Vue\n\nExtras: Which extras?  (more than one can be picked)\n  • Tests — xUnit", text);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"questions":[]}""")]
    [InlineData("""{"questions":"nope"}""")]
    public void WithoutQuestionsItStillTellsYouToLookAtTheTerminal(string input) =>
        Assert.Contains("terminal", ClaudeHook.QuestionDetail(Json(input)));

    [Fact]
    public async Task GotItEndsTheRequestWithoutAnAnswerSoTheTerminalKeepsTheQuestion()
    {
        var store = new ApprovalStore(new PushStore(NullLogger<PushStore>.Instance, Path.Combine(Path.GetTempPath(), "phonebox-q-" + Guid.NewGuid().ToString("N") + ".json")), NullLogger<ApprovalStore>.Instance);
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var a = new Approval("q1", "Claude Code", "s", "p", "Claude has a question", "text", null, now, now + 30_000, "question");
        var asking = store.AskAsync(a, CancellationToken.None);
        Assert.Equal("question", Assert.Single(store.Snapshot()).Kind);
        Assert.True(store.Decide("q1", "skip"));
        Assert.Equal("skip", await asking);
        Assert.False(store.Decide("q1", "skip")); // already finished
        Assert.False(store.Decide("q2", "maybe"));
    }
}
