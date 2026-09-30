using FF14Accessibility;
using FF14Accessibility.Services;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

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
        // Same base model is insufficient: material/variant must also match.
        Assert.Equal(0u, CharaMakeClassText.ResolveClass(0x0001002B00C9, equipment));
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

    [Theory]
    [InlineData("_CharaMakeCity", true, true)]
    [InlineData("_CharaMakeCity", false, false)]
    [InlineData("_CharaMakeClassSelector", true, false)]
    [InlineData("_CharaMakeGuardian", true, false)]
    public unsafe void AutomaticReaderMutesOnlyCityWhileClassSelectorIsVisible(string addonName, bool visible, bool expected)
    {
        AtkUnitBase selector = default;
        selector.IsVisible = visible;
        var gui = DispatchProxy.Create<IGameGui, CreationGui>();
        ((CreationGui)gui).Selector = (nint)(&selector);
        var reader = (UIReaderService)RuntimeHelpers.GetUninitializedObject(typeof(UIReaderService));
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(UIReaderService).GetField("_gameGui", fields)!.SetValue(reader, gui);
        typeof(UIReaderService).GetField("_config", fields)!.SetValue(reader, new Configuration());
        Assert.Equal(expected, typeof(UIReaderService).GetMethod("IsSuppressedAddon", fields)!.Invoke(reader, [addonName]));
        Assert.Equal(expected, typeof(UIReaderService).GetMethod("IsSuppressedCharaMakeCity", fields)!.Invoke(reader, [addonName]));
        if (expected)
        {
            // The real scanner must exit before touching any city nodes or speech services.
            typeof(UIReaderService).GetMethod("ScanAddonTexts", fields)!.Invoke(reader,
                [addonName, Pointer.Box(null, typeof(AtkUnitBase*)), false]);
        }
    }

    public class CreationGui : DispatchProxy
    {
        public nint Selector;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "GetAddonByName")
                return Activator.CreateInstance(method.ReturnType,
                    [(string)args![0]! == "_CharaMakeClassSelector" ? Selector : nint.Zero]);
            throw new NotSupportedException(method?.Name);
        }
    }
}
