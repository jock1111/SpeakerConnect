namespace SpeakerConnect.ViewModels;

/// <summary>Top-level navigation pages in the main window.</summary>
public enum AppPage
{
    Dashboard,
    Synchronization,
    VolumeBalance,
    Settings
}

/// <summary>One navigation entry shown in the sidebar.</summary>
public sealed record AppPageItem(AppPage Page, string Title, string Glyph);
