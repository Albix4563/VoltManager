namespace VoltManager.Tests;

public sealed class UpdateReadyEvaluationTests
{
    [Theory]
    [InlineData("""{"result":{"type":"boolean","value":true}}""", true)]
    [InlineData("""{"result":{"type":"boolean","value":false}}""", false)]
    // ExecuteScriptAsync's serialization of a pending promise must never count as ready.
    [InlineData("""{"result":{"type":"object","value":{}}}""", false)]
    [InlineData("""{"result":{"type":"object","subtype":"error"},"exceptionDetails":{"text":"Uncaught"}}""", false)]
    [InlineData("""{}""", false)]
    public void Readiness_requires_a_resolved_true_evaluation(string response, bool expected)
        => Assert.Equal(expected, MainWindow.IsConfirmedEvaluation(response));
}
