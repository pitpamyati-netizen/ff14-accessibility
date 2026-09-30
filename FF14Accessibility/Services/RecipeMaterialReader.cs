using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace FF14Accessibility.Services;

/// <summary>Reads one selected recipe; callbacks keep live inventory counts
/// separate from the byte-sized NQ/HQ fields in RecipeIngredient.</summary>
internal static unsafe class RecipeMaterialReader
{
    internal static bool MatchesSelection(string visible, string original, string localized) =>
        !string.IsNullOrWhiteSpace(visible) &&
        (string.Equals(visible.Trim(), original.Trim(), StringComparison.OrdinalIgnoreCase) ||
         string.Equals(visible.Trim(), localized.Trim(), StringComparison.OrdinalIgnoreCase));

    internal static List<string> Read(AddonRecipeNote* addon, RecipeNote.RecipeEntry* recipe,
        Func<uint, string> itemName, Func<string, string> translateLabel,
        Func<uint, bool, int> inventoryCount)
    {
        var lines = new List<string>();
        var useRuntime = recipe != null && MatchesSelection(
            AtkText.ReadClean(addon->SelectedRecipeName), AtkText.ReadClean(&recipe->ItemName),
            Loc.IsRussian ? itemName(recipe->ItemId) : string.Empty);

        if (useRuntime)
        {
            foreach (var ing in recipe->Ingredients)
            {
                // A partially populated list must not hide a required material.
                if ((ing.ItemId == 0) != (ing.Amount == 0))
                {
                    useRuntime = false;
                    break;
                }
            }
        }

        if (useRuntime)
        {
            foreach (var ing in recipe->Ingredients)
            {
                if (ing.ItemId == 0 || ing.Amount == 0) continue;
                // Names can still be loading in RecipeNote; the confirmed item
                // ID is sufficient to read the name and the full bag stock.
                lines.Add(AccessibilityStrings.RecipeMaterial(itemName(ing.ItemId),
                    ing.Amount.ToString(), Count(ing.ItemId, false), Count(ing.ItemId, true)));
            }
        }

        if (lines.Count == 0)
        {
            foreach (var ing in addon->Ingredients)
            {
                var name = AtkText.ReadClean(ing.Name).Trim();
                if (name.Length == 0) continue;
                lines.Add(AccessibilityStrings.RecipeMaterial(translateLabel(name),
                    AtkText.ReadClean(ing.QuantityRequiredForCraft),
                    AtkText.ReadClean(ing.QuantityInInventoryNq),
                    AtkText.ReadClean(ing.QuantityInInventoryHq)));
            }
        }

        for (var i = 0; i < addon->Crystals.Length; i++)
        {
            var crystal = addon->Crystals[i];
            var needed = AtkText.ReadClean(crystal.QuantityRequiredForCraft).Trim();
            if (useRuntime && i < recipe->Crystals.Length && recipe->Crystals[i].Amount > 0)
                needed = recipe->Crystals[i].Amount.ToString();
            if (needed.Length == 0 || needed == "0") continue;
            lines.Add(AccessibilityStrings.RecipeCrystal(needed,
                AtkText.ReadClean(crystal.QuantityInInventory)));
        }
        return lines;

        string Count(uint itemId, bool hq)
        {
            var value = inventoryCount(itemId, hq);
            return value >= 0 ? value.ToString() : string.Empty;
        }
    }
}
