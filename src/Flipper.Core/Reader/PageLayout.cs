namespace Flipper.Core.Reader;

public readonly record struct VisiblePages(int FirstIndex, int? SecondIndex, int Step);

public static class PageLayout
{
    public static bool IsPortrait(double width, double height) => width <= height;

    public static VisiblePages For(int pageCount, int lowestVisible, bool portrait)
    {
        if (pageCount <= 0)
        {
            return new VisiblePages(0, null, 1);
        }

        var page = Math.Clamp(lowestVisible, 0, pageCount - 1);
        if (portrait)
        {
            return new VisiblePages(page, null, 1);
        }

        var first = page % 2 == 0 ? page : page - 1;
        var second = first + 1;
        if (second >= pageCount)
        {
            return new VisiblePages(first, null, 2);
        }

        return new VisiblePages(first, second, 2);
    }

    public static int Turn(int lowestVisible, int pageCount, bool portrait, int direction)
    {
        if (pageCount <= 0)
        {
            return 0;
        }

        var current = For(pageCount, lowestVisible, portrait);
        var next = current.FirstIndex + (direction * current.Step);
        if (next < 0 || next >= pageCount)
        {
            return current.FirstIndex;
        }

        return next;
    }
}
