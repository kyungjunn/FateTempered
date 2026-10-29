using System;

// 마지막 판의 결과 보관함. 결과 화면이 읽는다.
public static class GameResult
{
    private static MatchResult? data;

    public static event Action Changed;

    public static MatchResult? Data
    {
        get => data;
        set
        {
            data = value;
            Changed?.Invoke();
        }
    }

    public static void Clear()
    {
        Data = null;
    }
}
