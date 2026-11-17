namespace TimeStore.Primitives;

public static class Clock
{
    extension(TimeProvider clock)
    {
        /// <summary>
        /// Current UTC time truncated to milliseconds: the precision every metadata provider stores, so a value
        /// returned on create equals the value read back later.
        /// </summary>
        public DateTime UtcNowMs
        {
            get
            {
                var now = clock.GetUtcNow().UtcDateTime;
                return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
            }
        }
    }
}
