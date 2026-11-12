namespace ATime.Windows
{
    /// <summary>Pure conversion of LASTINPUTINFO's 32-bit tick to the observation clock.</summary>
    public static class InputTickConverter
    {
        // GetTickCount64 is normally quantized to 10–16 ms. Allow at most two
        // timer quanta of API rounding, never an arbitrary future input timestamp.
        public const int MaximumFutureRoundingMs = 32;

        public static bool TryMap(uint inputTick, long sampledNowMs, out long inputMonotonicMs)
        {
            inputMonotonicMs = 0;
            if (sampledNowMs < 0) return false;
            var ahead = unchecked((int)(inputTick - (uint)sampledNowMs));
            if (ahead > 0)
            {
                if (ahead > MaximumFutureRoundingMs) return false;
                inputMonotonicMs = sampledNowMs;
                return true;
            }
            var age = unchecked((uint)sampledNowMs - inputTick);
            if (age > sampledNowMs) return false;
            inputMonotonicMs = sampledNowMs - age;
            return true;
        }
    }
}
