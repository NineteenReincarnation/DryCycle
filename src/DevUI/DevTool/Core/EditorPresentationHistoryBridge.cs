namespace DryCycle.DevUI.DevTool.Core;

/// <summary>
/// Keeps CorePresentation's retained history key synchronized when EditorHistoryService already
/// published the corresponding Shell/workspace revision at the mutation site. This prevents the
/// presentation hub from publishing the same semantic invalidation a second time later in the frame.
/// </summary>
public static partial class EditorPresentationHub
{
    internal static void ObservePublishedHistoryRevision(EditorSession session, long revision)
    {
        if (session == null || revision <= 0L)
            return;

        if (ReferenceEquals(observedSession, session))
            observedHistoryRevision = revision;
    }
}
