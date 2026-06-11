using TimeGrapher.App.Views;
using Xunit;

namespace TimeGrapher.App.Tests;

public sealed class MainWindowParseDoubleTests
{
    [Theory]
    [InlineData("500", 500.0)]
    [InlineData("3.5e2", 350.0)]
    [InlineData(" 12 ", 12.0)]
    [InlineData("-7.25", -7.25)]
    public void AcceptsCLocaleFloatGrammar(string text, double expected)
    {
        Assert.Equal(expected, MainWindow.ParseDouble(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0,5")]   // group separator is outside QString::toDouble's grammar
    [InlineData("(500)")] // so is parenthesized negation
    public void RejectsNonCLocaleInputWithZeroFallback(string? text)
    {
        Assert.Equal(0.0, MainWindow.ParseDouble(text));
    }
}
