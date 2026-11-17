using AgentLauncher.Core;

internal static class IdleWorkingSetChecks
{
    public static void Run(Action<string, Action> test, Action<bool, string> assert)
    {
        test("W3 idle trim uses elapsed time, not request count", () =>
        {
            var tracker = new IdleWorkingSetTracker();
            for (var i = 0; i < 1000; i++)
            {
                var now = TimeSpan.FromMilliseconds(i);
                tracker.Observe(42, now);
                assert(!tracker.TryBeginTrim(now), "rapid status polling shortened the idle window");
            }
            tracker.Observe(42, TimeSpan.FromSeconds(30));
            assert(tracker.TryBeginTrim(TimeSpan.FromSeconds(30)), "thirty seconds of observed idle did not allow trim");
        });
        test("W3 idle trim resets on activity, unknown accounting, or clock rollback", () =>
        {
            foreach (long? reading in new long?[] { 43, 0, null, -1 })
            {
                var tracker = new IdleWorkingSetTracker();
                tracker.Observe(42, TimeSpan.Zero);
                tracker.Observe(reading, TimeSpan.FromSeconds(31));
                assert(!tracker.TryBeginTrim(TimeSpan.FromSeconds(31)), "activity/unknown CPU authorized trim");
                tracker.Observe(44, TimeSpan.FromSeconds(32));
                tracker.Observe(44, TimeSpan.FromSeconds(61));
                assert(!tracker.TryBeginTrim(TimeSpan.FromSeconds(61)), "idle history survived an observation gap");
                tracker.Observe(44, TimeSpan.FromSeconds(62));
                assert(tracker.TryBeginTrim(TimeSpan.FromSeconds(62)), "idle did not recover after fresh accounting");
            }
            var rollback = new IdleWorkingSetTracker();
            rollback.Observe(1, TimeSpan.FromSeconds(100));
            rollback.Observe(1, TimeSpan.FromSeconds(90));
            assert(!rollback.TryBeginTrim(TimeSpan.FromSeconds(90)), "backward time allowed trim");
            assert(!rollback.TryBeginTrim(TimeSpan.FromSeconds(130)), "stale CPU observation allowed trim");
        });
        test("W3 trim attempts reserve cooldown even if native trimming fails", () =>
        {
            var tracker = new IdleWorkingSetTracker();
            tracker.Observe(42, TimeSpan.Zero);
            tracker.Observe(42, TimeSpan.FromSeconds(30));
            assert(tracker.TryBeginTrim(TimeSpan.FromSeconds(30)), "initial trim refused");
            for (var second = 30; second < 150; second++)
            {
                tracker.Observe(42, TimeSpan.FromSeconds(second));
                assert(!tracker.TryBeginTrim(TimeSpan.FromSeconds(second)), "failed/zero-byte attempt was immediately retried");
            }
            tracker.Observe(42, TimeSpan.FromSeconds(150));
            assert(tracker.TryBeginTrim(TimeSpan.FromSeconds(150)), "cooldown did not expire");
        });
    }
}
