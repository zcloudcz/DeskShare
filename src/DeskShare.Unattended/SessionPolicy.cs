namespace DeskShare.Unattended;

public enum SupervisorAction { None, Start, Stop, Restart }

/// <summary>Decides what the supervisor must do with the agent, given where it runs and which session owns the console.</summary>
public static class SessionPolicy
{
    /// <summary>WTSGetActiveConsoleSessionId returns this when no session is attached to the console (e.g. during a switch).</summary>
    public const uint NoSession = 0xFFFFFFFF;

    /// <summary>Session 0 is the isolated services session: never a capture target.</summary>
    public static bool IsUsable(uint activeConsoleSession) => activeConsoleSession != NoSession && activeConsoleSession != 0;

    /// <param name="runningSession">Session the agent currently runs in, or null when it is not running.</param>
    /// <param name="activeConsoleSession">Current console session id.</param>
    public static SupervisorAction Decide(uint? runningSession, uint activeConsoleSession)
    {
        if (!IsUsable(activeConsoleSession))
            return runningSession is null ? SupervisorAction.None : SupervisorAction.Stop;
        if (runningSession is null)
            return SupervisorAction.Start;
        return runningSession == activeConsoleSession ? SupervisorAction.None : SupervisorAction.Restart;
    }
}
