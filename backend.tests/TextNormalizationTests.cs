namespace backend.tests;

public class TextNormalizationTests
{
    [Theory]
    [InlineData("Química Analítica", "quimica analitica")]
    [InlineData("  REACCIÓN   ácido-base ", "reaccion acido-base")]
    [InlineData("CABINA", "cabina")]
    public void NormalizeForComparison_QuitaTildesYMayusculas(string input, string expected)
    {
        Assert.Equal(expected, TextNormalization.NormalizeForComparison(input));
    }

    [Fact]
    public void TokenizeSearch_DeduplicaYLimpiaTokens()
    {
        var tokens = TextNormalization.TokenizeSearch("  Química   quimica   CABINA ");

        Assert.Equal(["quimica", "cabina"], tokens);
    }

    [Fact]
    public void EscapeLikeToken_ProtegeCaracteresEspecialesDeLike()
    {
        var escaped = TextNormalization.EscapeLikeToken(@"50%_LAB\QA");

        Assert.Equal(@"50\%\_LAB\\QA", escaped);
    }
}
