using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Seamarks.Glassmaking;

// Glassmaking's workbench tool steps can require any item satisfying a Tags/wildcard match, exactly
// like a grid recipe ingredient, but its own handbook page and the in-world interaction hint both read
// CraftingRecipeIngredient.ResolvedItemStack unconditionally. That field is only ever populated by the
// engine for an ingredient with one exact item code (CraftingRecipeIngredient.Resolve leaves it null for
// anything MatchingType.Exact doesn't cover), so a tag-based tool step throws a null reference the moment
// either display path runs. Confirmed against Glassmaking's source (references/glassmaking) at both the
// version this integration was built against and the latest pull: neither ItemUseDescriptor nor
// ItemUseBehavior null-checks it.
//
// This shim only supplies the missing stack, read from a "seamarks:tooltipitem" key we add to the step's
// own recipeAttributes, the same JSON channel Glassmaking already uses for the blowtorch's temperature.
// It never touches the actual crafting match (CraftingRecipeIngredient.SatisfiesAsIngredient already
// handles Tags/wildcards correctly on its own), only the two display paths that crash without it. If
// Glassmaking ever null-checks ResolvedItemStack itself, this whole folder can be deleted: the attribute
// key becomes inert JSON Glassmaking doesn't read.
public static class WorkbenchTooltipFix
{
    private const string HarmonyId = "seamarks.glassmaking.workbenchtooltipfix";
    private const string TooltipItemAttribute = "seamarks:tooltipitem";

    private static ICoreClientAPI? capi;

    public static void Register(ICoreClientAPI api)
    {
        if (!api.ModLoader.IsModEnabled("glassmaking")) return;

        var descriptorType = FindType("GlassMaking.Workbench.ToolDescriptors.ItemUseDescriptor");
        var behaviorType = FindType("GlassMaking.Workbench.ToolBehaviors.ItemUseBehavior");
        if (descriptorType == null || behaviorType == null)
        {
            api.Logger.Warning("[seamarks] Glassmaking's ItemUseDescriptor/ItemUseBehavior weren't found by " +
                "reflection, the tool-step tooltip fix is skipped. Glassmaking's own workbench code may have " +
                "changed shape since this was written.");
            return;
        }

        capi = api;
        var harmony = new Harmony(HarmonyId);

        var handbookMethod = descriptorType.GetMethod("GetStepInfoForHandbook");
        if (handbookMethod != null)
        {
            harmony.Patch(handbookMethod, prefix: new HarmonyMethod(typeof(WorkbenchTooltipFix), nameof(HandbookPrefix)));
        }

        var interactionHelpMethod = behaviorType.GetMethod("GetBlockInteractionHelp");
        if (interactionHelpMethod != null)
        {
            harmony.Patch(interactionHelpMethod, prefix: new HarmonyMethod(typeof(WorkbenchTooltipFix), nameof(InteractionHelpPrefix)));
        }
    }

    // capi is only set once both early returns above are past, so it doubles as "we actually patched",
    // which keeps a dedicated server from building a Harmony instance just to unpatch nothing. Clearing it
    // also drops this static's hold on a client world that is going away.
    public static void Unregister()
    {
        if (capi == null) return;
        new Harmony(HarmonyId).UnpatchAll(HarmonyId);
        capi = null;
    }

    private static Type? FindType(string fullName) => AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType(fullName))
        .FirstOrDefault(t => t != null);

    // ItemUseDescriptor.GetStepInfoForHandbook(ICoreClientAPI capi, ItemStack item, WorkbenchRecipe recipe,
    // int stepIndex, CraftingRecipeIngredient? ingredient, ActionConsumable<string> openDetailPageFor,
    // List<RichTextComponentBase> outComponents). Only "ingredient" needs fixing up before the original runs.
    private static void HandbookPrefix(CraftingRecipeIngredient? ingredient) => FillFallbackStack(ingredient);

    // ItemUseBehavior.GetBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer
    // forPlayer, WorkbenchRecipe? recipe, int step). The ingredient it reads isn't a parameter here, it's
    // looked up from recipe.Steps[step].Tools[this.ToolCode], so this walks the same path by reflection.
    private static void InteractionHelpPrefix(object __instance, object? recipe, int step)
    {
        if (recipe == null) return;

        var toolCode = __instance.GetType().GetProperty("ToolCode")?.GetValue(__instance) as string;
        if (toolCode == null) return;

        var stepsField = recipe.GetType().GetField("Steps");
        var steps = stepsField?.GetValue(recipe) as Array;
        if (steps == null || step < 0 || step >= steps.Length) return;

        var stepObj = steps.GetValue(step);
        var toolsField = stepObj?.GetType().GetField("Tools");
        if (toolsField?.GetValue(stepObj) is not Dictionary<string, CraftingRecipeIngredient?> tools) return;

        if (tools.TryGetValue(toolCode, out var ingredient)) FillFallbackStack(ingredient);
    }

    private static void FillFallbackStack(CraftingRecipeIngredient? ingredient)
    {
        if (ingredient == null || ingredient.ResolvedItemStack != null || capi == null) return;

        string? code = ingredient.RecipeAttributes?[TooltipItemAttribute].AsString();
        if (code == null) return;

        var item = capi.World.GetItem(new AssetLocation(code));
        if (item != null) ingredient.ResolvedItemStack = new ItemStack(item);
    }
}
