namespace HikIsapi;

/// <summary>
/// Widths for one console row. Flex fields take the leftover space, and shrink
/// before a button is pushed onto a clipped second line.
/// </summary>
public static class RowFlow
{
    public readonly record struct Piece(int Natural, int Minimum, int MarginX, int MarginY, int Height, bool Flex);

    public readonly record struct Arrangement(int[] Widths, int Height);

    public static Arrangement Arrange(IReadOnlyList<Piece> pieces, int available)
    {
        available = Math.Max(available, 1);
        var widths = new int[pieces.Count];
        var fixedOccupied = 0;
        var flexCount = 0;
        var flexMinimum = 0;
        var flexMargin = 0;
        for (var index = 0; index < pieces.Count; index++)
        {
            var piece = pieces[index];
            if (piece.Flex)
            {
                flexCount++;
                flexMinimum += Math.Max(piece.Minimum, 1);
                flexMargin += NonNegative(piece.MarginX);
                continue;
            }

            var cap = Math.Max(1, available - NonNegative(piece.MarginX));
            widths[index] = Math.Min(Math.Max(piece.Natural, 1), cap);
            fixedOccupied += widths[index] + NonNegative(piece.MarginX);
        }

        if (flexCount > 0)
        {
            var inner = available - fixedOccupied - flexMargin;
            if (inner >= flexMinimum)
                Distribute(pieces, widths, inner - flexMinimum, useMinimum: true);
            else if (inner >= flexCount)
                Distribute(pieces, widths, inner - flexCount, useMinimum: false);
            else
            {
                for (var index = 0; index < pieces.Count; index++)
                {
                    if (!pieces[index].Flex)
                        continue;
                    var cap = Math.Max(1, available - NonNegative(pieces[index].MarginX));
                    widths[index] = Math.Min(Math.Max(pieces[index].Minimum, 1), cap);
                }
            }
        }

        return new Arrangement(widths, MeasureHeight(pieces, widths, available));
    }

    private static void Distribute(IReadOnlyList<Piece> pieces, int[] widths, int extra, bool useMinimum)
    {
        var flexCount = 0;
        for (var index = 0; index < pieces.Count; index++)
        {
            if (pieces[index].Flex)
                flexCount++;
        }

        if (flexCount == 0)
            return;
        var share = Math.Max(extra, 0) / flexCount;
        var rest = Math.Max(extra, 0) % flexCount;
        var seen = 0;
        for (var index = 0; index < pieces.Count; index++)
        {
            if (!pieces[index].Flex)
                continue;
            var floor = useMinimum ? Math.Max(pieces[index].Minimum, 1) : 1;
            widths[index] = floor + share + (seen < rest ? 1 : 0);
            seen++;
        }
    }

    private static int MeasureHeight(IReadOnlyList<Piece> pieces, int[] widths, int available)
    {
        var x = 0;
        var y = 0;
        var line = 0;
        for (var index = 0; index < pieces.Count; index++)
        {
            var occupied = widths[index] + NonNegative(pieces[index].MarginX);
            if (x > 0 && x + occupied > available)
            {
                y += line;
                x = 0;
                line = 0;
            }

            x += occupied;
            line = Math.Max(line, Math.Max(pieces[index].Height, 1) + NonNegative(pieces[index].MarginY));
        }

        return y + line;
    }

    private static int NonNegative(int value) => Math.Max(value, 0);
}
