namespace VidFlow.Services;

/// <summary>What to do with SponsorBlock segments (sponsor reads, self-promotion, intros…) in YouTube videos.</summary>
public static class SponsorBlockModes
{
    public const string Off = "off";
    /// <summary>Keep everything; the segments become chapters to skip by hand.</summary>
    public const string Mark = "mark";
    /// <summary>Cut the segments out of the file.</summary>
    public const string Remove = "remove";

    /// <summary>Cut: sponsor reads, self-promotion, "like and subscribe", intro and outro. Kept: off-topic parts,
    /// previews/recaps and filler, which viewers often want.</summary>
    public const string RemovedCategories = "sponsor,selfpromo,interaction,intro,outro";
}
