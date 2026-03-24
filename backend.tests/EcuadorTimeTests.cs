namespace backend.tests;

public class EcuadorTimeTests
{
    [Fact]
    public void ParseTimestamp_ConOffsetUtc_NormalizaAHorarioDeEcuador()
    {
        var parsed = EcuadorTime.ParseTimestamp("2026-03-24T18:15:00Z");

        Assert.NotNull(parsed);
        Assert.Equal(TimeSpan.FromHours(-5), parsed.Value.Offset);
        Assert.Equal(13, parsed.Value.Hour);
        Assert.Equal(15, parsed.Value.Minute);
    }

    [Fact]
    public void ParseTimestamp_SinOffset_PresumeHorarioLocalDeEcuador()
    {
        var parsed = EcuadorTime.ParseTimestamp("2026-03-24 08:30");

        Assert.NotNull(parsed);
        Assert.Equal(TimeSpan.FromHours(-5), parsed.Value.Offset);
        Assert.Equal(8, parsed.Value.Hour);
        Assert.Equal(30, parsed.Value.Minute);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("texto-invalido")]
    public void ParseTimestamp_ValoresInvalidos_RetornaNull(string? value)
    {
        Assert.Null(EcuadorTime.ParseTimestamp(value));
    }
}
