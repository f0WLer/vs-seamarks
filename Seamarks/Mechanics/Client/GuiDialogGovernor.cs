using Seamarks.Mechanics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Seamarks.Mechanics.Client;

// Right-click panel for the one thing the Governor exposes: the speed ceiling. The slider counts in
// hundredths of a revolution per second, so it lands on clean 0.05 steps, and every reading shown to the
// player is scaled back to rps.
public class GuiDialogGovernor : GuiDialog
{
    private const int Hundredths = 100;
    private const int StepHundredths = 5; // 0.05 rps per notch

    private static readonly AssetLocation TickSoundLoc = new(SeamarksModSystem.Domain, "sounds/brass-tick");

    private readonly BlockPos pos;
    private readonly int minHundredths;
    private readonly int maxHundredths;
    private float maxSpeed;

    public override string ToggleKeyCombinationCode => "seamarksgovernordialog";

    public GuiDialogGovernor(ICoreClientAPI capi, BlockPos pos, float minSpeed, float maxSpeed, float current)
        : base(capi)
    {
        this.pos = pos;
        minHundredths = (int)MathF.Round(minSpeed * Hundredths);
        maxHundredths = (int)MathF.Round(maxSpeed * Hundredths);
        this.maxSpeed = current;
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();

        ElementBounds textBounds = ElementBounds.Fixed(0, 10, 260, 25);
        ElementBounds sliderBounds = ElementBounds.Fixed(0, 0, 260, 22).FixedUnder(textBounds, 8);

        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        bgBounds.WithChildren(textBounds, sliderBounds);

        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

        SingleComposer = capi.Gui.CreateCompo("seamarksgovernor" + pos, dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(Lang.Get("seamarks:gui-governor-title"), () => TryClose())
            .BeginChildElements(bgBounds)
                .AddDynamicText(ValueText(), CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center), textBounds, "value")
                .AddSlider(OnSliderChanged, sliderBounds, "speed")
            .EndChildElements()
            .Compose();

        var slider = SingleComposer.GetSlider("speed");
        slider.SetValues(GameMath.Clamp((int)MathF.Round(maxSpeed * Hundredths), minHundredths, maxHundredths), minHundredths, maxHundredths, StepHundredths);
        slider.OnSliderTooltip = FormatHundredths;
        slider.OnSliderRestingText = FormatHundredths;
    }

    private string FormatHundredths(int value) => Lang.Get("seamarks:gui-governor-value", value / (float)Hundredths);

    private bool OnSliderChanged(int hundredths)
    {
        float newSpeed = hundredths / (float)Hundredths;
        if (newSpeed != maxSpeed) capi.Gui.PlaySound(TickSoundLoc, true);
        maxSpeed = newSpeed;
        SingleComposer.GetDynamicText("value").SetNewText(ValueText());
        capi.Network.SendBlockEntityPacket(pos, BlockEntityGovernor.SetSpeedPacketId, BitConverter.GetBytes(maxSpeed));
        return true;
    }

    private string ValueText() => Lang.Get("seamarks:gui-governor-value", maxSpeed);
}
