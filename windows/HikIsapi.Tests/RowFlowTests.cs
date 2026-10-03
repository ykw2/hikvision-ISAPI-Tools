namespace HikIsapi.Tests;

public sealed class RowFlowTests
{
    [Fact]
    public void KeepsTheButtonOnTheChannelRow()
    {
        // Label, a combo that would rather be 360px, and 啟動串流. The pane is
        // only wide enough if the combo gives up its preferred width.
        var pieces = new RowFlow.Piece[]
        {
            new(Natural: 82, Minimum: 0, MarginX: 8, MarginY: 8, Height: 22, Flex: false),
            new(Natural: 0, Minimum: 360, MarginX: 8, MarginY: 4, Height: 30, Flex: true),
            new(Natural: 130, Minimum: 0, MarginX: 8, MarginY: 4, Height: 32, Flex: false),
        };

        var laid = RowFlow.Arrange(pieces, 547);

        Assert.Equal(130, laid.Widths[2]);
        Assert.True(laid.Widths[1] < 360);
        Assert.True(laid.Widths[1] >= 120);
        Assert.Equal(547, 82 + 8 + laid.Widths[1] + 8 + 130 + 8);
        Assert.Equal(32 + 4, laid.Height);
    }

    [Fact]
    public void GrowsFlexFieldsWhenTheRowIsWide()
    {
        var pieces = new RowFlow.Piece[]
        {
            new(Natural: 40, Minimum: 0, MarginX: 8, MarginY: 4, Height: 20, Flex: false),
            new(Natural: 0, Minimum: 80, MarginX: 8, MarginY: 4, Height: 28, Flex: true),
            new(Natural: 40, Minimum: 0, MarginX: 8, MarginY: 4, Height: 20, Flex: false),
            new(Natural: 0, Minimum: 80, MarginX: 8, MarginY: 4, Height: 28, Flex: true),
        };

        var laid = RowFlow.Arrange(pieces, 400);

        Assert.Equal(40, laid.Widths[0]);
        Assert.Equal(40, laid.Widths[2]);
        Assert.True(laid.Widths[1] > 80);
        Assert.Equal(laid.Widths[1], laid.Widths[3]);
        Assert.Equal(28 + 4, laid.Height);
    }

    [Fact]
    public void WrapsInsteadOfClippingWhenNothingFits()
    {
        var pieces = new RowFlow.Piece[]
        {
            new(Natural: 90, Minimum: 0, MarginX: 8, MarginY: 4, Height: 20, Flex: false),
            new(Natural: 130, Minimum: 0, MarginX: 8, MarginY: 4, Height: 32, Flex: false),
            new(Natural: 0, Minimum: 200, MarginX: 8, MarginY: 4, Height: 28, Flex: true),
        };

        var laid = RowFlow.Arrange(pieces, 200);

        Assert.Equal(90, laid.Widths[0]);
        Assert.Equal(130, laid.Widths[1]);
        Assert.True(laid.Widths[2] <= 200);
        Assert.True(laid.Height >= 20 + 4 + 32 + 4);
    }
}
