using GeroImperium.Core.Http;

namespace GeroImperium.Core.Tests.Http;

public class ChordSyntaxTests
{
    // Worked examples straight from doc/windows_app_api_guide.md's chord syntax table.

    [Fact]
    public void Parse_SingleModifierAndKey()
    {
        var steps = ChordSyntax.Parse("[ctrl]+c");

        var step = Assert.Single(steps);
        Assert.True(step.Ctrl);
        Assert.False(step.Shift);
        Assert.Equal(["c"], step.Keys);
    }

    [Fact]
    public void Parse_MultipleModifiersAndKeys_AllInOneStep()
    {
        var steps = ChordSyntax.Parse("[ctrl]+[shift]+b+n");

        var step = Assert.Single(steps);
        Assert.True(step.Ctrl);
        Assert.True(step.Shift);
        Assert.Equal(["b", "n"], step.Keys);
    }

    [Fact]
    public void Parse_NoPlusBeforeBracket_StartsANewStep()
    {
        var steps = ChordSyntax.Parse("[ctrl]+k[ctrl]+l");

        Assert.Equal(2, steps.Count);
        Assert.True(steps[0].Ctrl);
        Assert.Equal(["k"], steps[0].Keys);
        Assert.True(steps[1].Ctrl);
        Assert.Equal(["l"], steps[1].Keys);
    }

    [Fact]
    public void Parse_BareKeyNoModifier()
    {
        var steps = ChordSyntax.Parse("D");

        var step = Assert.Single(steps);
        Assert.False(step.Ctrl);
        Assert.False(step.Shift);
        Assert.False(step.Alt);
        Assert.False(step.Win);
        Assert.Equal(["D"], step.Keys);
    }

    [Fact]
    public void Parse_FnModifier_IsAcceptedAndIgnored()
    {
        var steps = ChordSyntax.Parse("[fn]+F5");

        var step = Assert.Single(steps);
        Assert.False(step.Ctrl);
        Assert.Equal(["F5"], step.Keys);
    }

    [Fact]
    public void Parse_ModifiersAreCaseInsensitive()
    {
        var steps = ChordSyntax.Parse("[CTRL]+[Shift]+a");

        var step = Assert.Single(steps);
        Assert.True(step.Ctrl);
        Assert.True(step.Shift);
    }

    [Theory]
    [InlineData("[ctrl]+c")]
    [InlineData("[ctrl]+[shift]+b+n")]
    [InlineData("[ctrl]+k[ctrl]+l")]
    [InlineData("D")]
    public void Build_RoundTripsParse(string textContent)
    {
        var steps = ChordSyntax.Parse(textContent);

        Assert.Equal(textContent, ChordSyntax.Build(steps));
    }

    [Fact]
    public void Parse_UnterminatedBracket_Throws()
    {
        Assert.Throws<FormatException>(() => ChordSyntax.Parse("[ctrl+c"));
    }

    [Fact]
    public void Parse_DanglingPlus_Throws()
    {
        Assert.Throws<FormatException>(() => ChordSyntax.Parse("[ctrl]+c+"));
    }

    [Fact]
    public void TryParse_MalformedInput_ReturnsFalse()
    {
        var result = ChordSyntax.TryParse("[ctrl+c", out var steps);

        Assert.False(result);
        Assert.Empty(steps);
    }

    [Fact]
    public void Validate_WellFormedKnownTokens_ReturnsEmpty()
    {
        var issues = ChordSyntax.Validate("[ctrl]+[shift]+ENTER");

        Assert.Empty(issues);
    }

    [Fact]
    public void Validate_UnknownModifier_IsFlagged()
    {
        var issues = ChordSyntax.Validate("[meta]+c");

        Assert.Contains("[meta]", issues);
    }

    [Fact]
    public void Validate_UnknownBareKey_IsFlagged()
    {
        var issues = ChordSyntax.Validate("[ctrl]+banana");

        Assert.Contains("banana", issues);
    }

    [Fact]
    public void Validate_NamedKeysAndFunctionKeys_AreRecognized()
    {
        Assert.Empty(ChordSyntax.Validate("ENTER"));
        Assert.Empty(ChordSyntax.Validate("F12"));
        Assert.Empty(ChordSyntax.Validate("[fn]+F5"));
    }

    [Fact]
    public void Validate_MalformedInput_ReportsAnIssue()
    {
        var issues = ChordSyntax.Validate("[ctrl+c");

        Assert.NotEmpty(issues);
    }
}
