namespace NewScope.Services;

public readonly record struct WindowWorkArea(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

public static class WindowPlacementPolicy
{
    public static (int X, int Y) Clamp(int x, int y, int width, int height, WindowWorkArea area)
    {
        var maxX = Math.Max(area.X, area.Right - Math.Max(1, width));
        var maxY = Math.Max(area.Y, area.Bottom - Math.Max(1, height));
        return (Math.Clamp(x, area.X, maxX), Math.Clamp(y, area.Y, maxY));
    }
}
