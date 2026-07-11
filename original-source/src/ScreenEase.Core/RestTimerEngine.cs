namespace ScreenEase.Core;

public static class RestTimerEngine
{
    public static RestTimerState Start(RestTimerSettings settings, DateTimeOffset now)
    {
        var normalized = Validation.Normalize(settings);
        return new RestTimerState(
            Phase: RestTimerPhase.Work,
            StartedAt: now,
            EndsAt: now.AddMinutes(normalized.WorkMinutes),
            PausedRemaining: null,
            PausedFrom: null,
            CompletedWorkSessions: 0);
    }

    public static RestTimerState Pause(RestTimerState state, DateTimeOffset now)
    {
        if (state.Phase is RestTimerPhase.Stopped or RestTimerPhase.Paused)
        {
            return state;
        }

        var remaining = state.EndsAt is null
            ? TimeSpan.Zero
            : state.EndsAt.Value - now;

        return state with
        {
            Phase = RestTimerPhase.Paused,
            PausedRemaining = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
            PausedFrom = state.Phase,
            EndsAt = null
        };
    }

    public static RestTimerState Resume(RestTimerState state, DateTimeOffset now)
    {
        if (state.Phase != RestTimerPhase.Paused)
        {
            return state;
        }

        var remaining = state.PausedRemaining ?? TimeSpan.Zero;
        return state with
        {
            Phase = state.PausedFrom ?? RestTimerPhase.Work,
            StartedAt = now,
            EndsAt = now.Add(remaining),
            PausedRemaining = null,
            PausedFrom = null
        };
    }

    public static RestTimerState Reset() => Defaults.CreateRestTimerState();

    public static RestTimerState Tick(RestTimerState state, RestTimerSettings settings, DateTimeOffset now)
    {
        var normalized = Validation.Normalize(settings);
        if (!normalized.Enabled)
        {
            return Reset();
        }

        if (state.Phase == RestTimerPhase.Stopped)
        {
            return normalized.AutoStart ? Start(normalized, now) : state;
        }

        if (state.Phase == RestTimerPhase.Paused || state.EndsAt is null || now < state.EndsAt.Value)
        {
            return state;
        }

        return state.Phase switch
        {
            RestTimerPhase.Work => StartBreak(state, normalized, now),
            RestTimerPhase.ShortBreak or RestTimerPhase.LongBreak => StartWork(state, normalized, now),
            _ => state
        };
    }

    private static RestTimerState StartBreak(RestTimerState state, RestTimerSettings settings, DateTimeOffset now)
    {
        var completed = state.CompletedWorkSessions + 1;
        var isLongBreak = completed % settings.LongBreakEveryWorkSessions == 0;
        var minutes = isLongBreak ? settings.LongBreakMinutes : settings.ShortBreakMinutes;

        return new RestTimerState(
            Phase: isLongBreak ? RestTimerPhase.LongBreak : RestTimerPhase.ShortBreak,
            StartedAt: now,
            EndsAt: now.AddMinutes(minutes),
            PausedRemaining: null,
            PausedFrom: null,
            CompletedWorkSessions: completed);
    }

    private static RestTimerState StartWork(RestTimerState state, RestTimerSettings settings, DateTimeOffset now) =>
        new(
            Phase: RestTimerPhase.Work,
            StartedAt: now,
            EndsAt: now.AddMinutes(settings.WorkMinutes),
            PausedRemaining: null,
            PausedFrom: null,
            CompletedWorkSessions: state.CompletedWorkSessions);
}


