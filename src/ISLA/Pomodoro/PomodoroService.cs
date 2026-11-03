using System.Text.Json;
using System.IO;

namespace ISLA.Pomodoro;

public enum PomodoroPhase
{
    Focus,
    ShortBreak,
    LongBreak
}

public sealed class PomodoroState
{
    public PomodoroPhase Phase { get; set; } = PomodoroPhase.Focus;
    public int CompletedFocusSessions { get; set; }
    public bool IsRunning { get; set; }
    public DateTime? EndsAtUtc { get; set; }
    public TimeSpan Remaining { get; set; } = TimeSpan.FromMinutes(25);

    // Chosen durations (minutes). Old pomodoro.json files without these keep the defaults.
    public int FocusMinutes { get; set; } = 25;
    public int ShortBreakMinutes { get; set; } = 5;
    public int LongBreakMinutes { get; set; } = 15;

    // Hold Windows notifications while a focus session is running.
    public bool FocusModeEnabled { get; set; }
}

public sealed class PomodoroService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ISLA",
        "pomodoro.json");

    public PomodoroState State { get; private set; } = new();

    /// <summary>Phase and length (minutes) of the session that just ended, set when <see cref="Update"/> returns true.</summary>
    public PomodoroPhase LastCompletedPhase { get; private set; }
    public int LastCompletedMinutes { get; private set; }

    public PomodoroService()
    {
        Load();
    }

    public bool Update()
    {
        if (!State.IsRunning || State.EndsAtUtc is not DateTime endsAt)
        {
            return false;
        }

        State.Remaining = endsAt - DateTime.UtcNow;
        if (State.Remaining > TimeSpan.Zero)
        {
            return false;
        }

        State.Remaining = TimeSpan.Zero;
        State.IsRunning = false;
        State.EndsAtUtc = null;
        LastCompletedPhase = State.Phase;
        LastCompletedMinutes = (int)PhaseDuration(State.Phase).TotalMinutes;
        AdvancePhase();
        Save();
        return true;
    }

    public void StartOrResume()
    {
        if (State.IsRunning)
        {
            return;
        }

        State.EndsAtUtc = DateTime.UtcNow + State.Remaining;
        State.IsRunning = true;
        Save();
    }

    public void Pause()
    {
        Update();
        if (!State.IsRunning)
        {
            return;
        }

        State.Remaining = State.EndsAtUtc!.Value - DateTime.UtcNow;
        State.IsRunning = false;
        State.EndsAtUtc = null;
        Save();
    }

    public void Reset()
    {
        State.IsRunning = false;
        State.EndsAtUtc = null;
        State.Remaining = PhaseDuration(State.Phase);
        Save();
    }

    /// <summary>Sets the length of a phase. If that phase is the one on the timer (and it is not running), the timer shows the new length at once.</summary>
    public void SetDuration(PomodoroPhase phase, int minutes)
    {
        minutes = Math.Clamp(minutes, 1, 180);
        switch (phase)
        {
            case PomodoroPhase.ShortBreak:
                State.ShortBreakMinutes = minutes;
                break;
            case PomodoroPhase.LongBreak:
                State.LongBreakMinutes = minutes;
                break;
            default:
                State.FocusMinutes = minutes;
                break;
        }

        if (State.Phase == phase && !State.IsRunning)
        {
            State.Remaining = PhaseDuration(phase);
        }

        Save();
    }

    /// <summary>Switches the timer to Focus / Short break / Long break. Ignored while the timer is running.</summary>
    public void SelectPhase(PomodoroPhase phase)
    {
        if (State.IsRunning || State.Phase == phase)
        {
            return;
        }

        State.Phase = phase;
        State.Remaining = PhaseDuration(phase);
        Save();
    }

    public void SetFocusMode(bool enabled)
    {
        State.FocusModeEnabled = enabled;
        Save();
    }

    private void AdvancePhase()
    {
        if (State.Phase == PomodoroPhase.Focus)
        {
            State.CompletedFocusSessions++;
            State.Phase = State.CompletedFocusSessions % 4 == 0
                ? PomodoroPhase.LongBreak
                : PomodoroPhase.ShortBreak;
        }
        else
        {
            State.Phase = PomodoroPhase.Focus;
        }

        State.Remaining = PhaseDuration(State.Phase);
    }

    public TimeSpan PhaseDuration(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.ShortBreak => TimeSpan.FromMinutes(Math.Clamp(State.ShortBreakMinutes, 1, 180)),
        PomodoroPhase.LongBreak => TimeSpan.FromMinutes(Math.Clamp(State.LongBreakMinutes, 1, 180)),
        _ => TimeSpan.FromMinutes(Math.Clamp(State.FocusMinutes, 1, 180))
    };

    private void Load()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return;
            }

            var saved = JsonSerializer.Deserialize<PomodoroState>(File.ReadAllText(_statePath));
            if (saved is null)
            {
                return;
            }

            State = saved;
            Update();
        }
        catch (JsonException)
        {
            State = new PomodoroState();
        }
        catch (IOException)
        {
            State = new PomodoroState();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(State, SerializerOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A locked or read-only state file must never crash the timer; it is only persistence.
        }
    }
}
