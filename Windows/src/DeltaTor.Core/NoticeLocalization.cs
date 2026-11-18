namespace DeltaTor.Core;

/// <summary>
/// Picks the one block of a notice that matches the language chosen at
/// startup.
///
/// The first-run and chain-intro notices used to carry all three languages at
/// once, because nothing knew which one the user read. The picker answers that
/// now, and the other two would be exactly the wall of text the picker exists
/// to avoid.
/// </summary>
public static class NoticeLocalization
{
    public static IReadOnlyList<NoticeBlock> Pick(
        NoticeBlock english,
        NoticeBlock russian,
        NoticeBlock persian) => new[]
    {
        Config.Language switch
        {
            Config.LanguageRussian => russian,
            Config.LanguagePersian => persian,
            _ => english
        }
    };
}