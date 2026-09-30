using FF14Accessibility;
using FF14Accessibility.Services;

namespace Regression.Tests;

[Collection("Language")]
public sealed class CharaMakeTests
{
    private static readonly CharaMakeClassText.Entry Gladiator = new(1, "Гладиатор", "Описание гладиатора");
    private static readonly CharaMakeClassText.Entry Thaumaturge = new(7, "Тауматург", "Описание тауматурга");

    [Fact]
    public void GuardianDescriptionCannotSurviveEntryIntoClassSelection()
    {
        var buffer = new CharaMakeHelpBuffer();
        buffer.Queue("_CharaMakeGuardian", "Описание Нофики", 0);
        Assert.Empty(buffer.Take("_CharaMakeClassSelector", "Описание Нофики", 400));
        buffer.Queue("_CharaMakeClassSelector", "Описание Нофики", 500);
        Assert.Empty(buffer.Take("_CharaMakeClassSelector", "Описание Нофики", 900, force: true));
        Assert.False(buffer.HasPending);
    }

    [Theory]
    [InlineData("", "Прежнее описание")]
    [InlineData("_CharaMakeTribe", "Прежнее описание")]
    [InlineData("_CharaMakeRaceGender", "Другое описание")]
    [InlineData("_CharaMakeRaceGender", "")]
    public void ClosedHiddenOrChangedHelpCancelsPendingText(string owner, string text)
    {
        var buffer = new CharaMakeHelpBuffer();
        buffer.Queue("_CharaMakeRaceGender", "Прежнее описание", 0);
        Assert.Empty(buffer.Take(owner, text, 500, force: true));
        Assert.False(buffer.HasPending);
    }

    [Fact]
    public void RaceHelpStillWaitsForHeadlineAndSpeaksOnce()
    {
        var buffer = new CharaMakeHelpBuffer();
        buffer.Queue("_CharaMakeRaceGender", "Описание расы", 100);
        Assert.Empty(buffer.Take("_CharaMakeRaceGender", "Описание расы", 200));
        Assert.Equal("Описание расы", buffer.Take("_CharaMakeRaceGender", "Описание расы", 200, true));
        Assert.Empty(buffer.Take("_CharaMakeRaceGender", "Описание расы", 500));
        buffer.Queue("_CharaMakeTribe", "Описание племени", 600);
        Assert.Equal("Описание племени", buffer.Take("_CharaMakeTribe", "Описание племени", 850));
    }

    [Fact]
    public void RapidClassChangeDescribesOnlyTheLatestClassAfterItsName()
    {
        var speech = new CharaMakeClassSpeech();
        Assert.Equal((Gladiator.Name, ""), speech.Update(Gladiator, 0));
        Assert.Equal(("", ""), speech.Update(Gladiator, 200));
        Assert.Equal((Thaumaturge.Name, ""), speech.Update(Thaumaturge, 250));
        Assert.Equal(("", ""), speech.Update(Thaumaturge, 400));
        Assert.Equal(("", Thaumaturge.Description), speech.Update(Thaumaturge, 600));
        Assert.Equal(("", ""), speech.Update(Thaumaturge, 1000));
    }

    [Fact]
    public void ClosingClassStepOrLosingPreviewCancelsAndReopeningRepeats()
    {
        var speech = new CharaMakeClassSpeech();
        speech.Update(Gladiator, 0);
        Assert.Equal(("", ""), speech.Update(null, 200));
        Assert.Equal(("", ""), speech.Update(null, 600));
        Assert.Equal((Gladiator.Name, ""), speech.Update(Gladiator, 800));
        Assert.Equal(("", Gladiator.Description), speech.Update(Gladiator, 1150));
    }

    [Fact]
    public void ManualRepeatReadsFreshClassAndConsumesDelayedSpeech()
    {
        var speech = new CharaMakeClassSpeech();
        speech.Update(Gladiator, 0);
        Assert.Equal((Thaumaturge.Name, Thaumaturge.Description), speech.Update(Thaumaturge, 100, true));
        Assert.Equal(("", ""), speech.Update(Thaumaturge, 600));
        Assert.Equal((Thaumaturge.Name, Thaumaturge.Description), speech.Update(Thaumaturge, 800, true));
    }

    [Fact]
    public void MissingAndLateDescriptionNeverBorrowsPreviousClassText()
    {
        var speech = new CharaMakeClassSpeech();
        speech.Update(Gladiator, 0);
        var missing = Thaumaturge with { Description = "" };
        Assert.Equal((missing.Name, ""), speech.Update(missing, 100));
        Assert.Equal(("", ""), speech.Update(missing, 600));
        speech.Update(Thaumaturge, 800);
        Assert.Equal(("", Thaumaturge.Description), speech.Update(Thaumaturge, 1150));
    }

    [Fact]
    public void WeaponMatchIgnoresDyesButRejectsUnknownAndAmbiguousWeapons()
    {
        CharaMakeClassText.Equipment[] equipment = [new(0x0001000A00C9, 1), new(0x0001000203E9, 7)];
        Assert.Equal(1u, CharaMakeClassText.ResolveClass(0xABCD0001000A00C9, equipment));
        Assert.Equal(7u, CharaMakeClassText.ResolveClass(0x0001000203E9, equipment));
        Assert.Equal(0u, CharaMakeClassText.ResolveClass(123, equipment));
        Assert.Equal(0u, CharaMakeClassText.ResolveClass(0, [new(0, 1)]));
        Assert.Equal(0u, CharaMakeClassText.ResolveClass(123, [new(123, 1), new(123, 7)]));
    }

    [Theory]
    [InlineData(1u, 178u)] [InlineData(2u, 180u)] [InlineData(3u, 182u)] [InlineData(4u, 184u)]
    [InlineData(5u, 186u)] [InlineData(6u, 188u)] [InlineData(7u, 190u)] [InlineData(26u, 192u)]
    [InlineData(0u, 0u)] [InlineData(19u, 0u)]
    public void OnlyStartingClassesHaveDescriptionRows(uint id, uint expected)
        => Assert.Equal(expected, CharaMakeClassText.LobbyRow(id));

    [Theory]
    [InlineData("gladiator", "Gladiator", true)]
    [InlineData("thaumaturge", "Gladiator", false)]
    [InlineData("gladiator", "Nophica", false)]
    [InlineData("", "", false)]
    public void ChangedSourceHeadingCannotBeAccepted(string job, string lobby, bool expected)
        => Assert.Equal(expected, CharaMakeClassText.HeadingMatches(job, lobby));

    [Theory]
    [InlineData(LanguageMode.Russian, "Выбранный класс")]
    [InlineData(LanguageMode.English, "The selected class")]
    [InlineData(LanguageMode.German, "Die ausgewählte Klasse")]
    public void UnavailableSelectionUsesChosenLanguage(LanguageMode mode, string prefix)
    {
        var previous = Loc.Mode;
        try { Loc.Mode = mode; Assert.StartsWith(prefix, AccessibilityStrings.CharaMakeClassUnavailable); }
        finally { Loc.Mode = previous; }
    }
}
