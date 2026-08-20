namespace Flipper.Core.Reader;

public static class PageTurnGesture
{
    public const double SwipeThreshold = 80;

    public static PageTurnCommand FromTap(double x, double width)
    {
        if (width <= 0)
        {
            return PageTurnCommand.None;
        }

        return x < width / 2 ? PageTurnCommand.Back : PageTurnCommand.Next;
    }

    public static PageTurnCommand FromSwipe(double translationX, double threshold = SwipeThreshold)
    {
        if (Math.Abs(translationX) <= threshold)
        {
            return PageTurnCommand.None;
        }

        return translationX < 0 ? PageTurnCommand.Next : PageTurnCommand.Back;
    }
}
