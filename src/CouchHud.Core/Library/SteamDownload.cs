namespace CouchHud.Core.Library;

public enum SteamDownloadState
{
    Downloading,
    Paused,
    /// <summary>Waiting in Steam's queue, or an update Steam hasn't started yet.</summary>
    Queued
}

/// <summary>A game Steam is installing or updating.</summary>
/// <param name="IsUpdate">True for an update of an installed game, false for a first install.</param>
public sealed record SteamDownload(string AppId, string Name, SteamDownloadState State, double Progress, long BytesDone, long BytesTotal, bool IsUpdate);
