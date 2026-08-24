using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Seamarks.Beacon.Client;

// The calibration panel: a light GuiDialog with one slider per BeamTuning knob, so the
// beam/point-light/dust look can be dialed in live and watched in-world. Moving a slider just calls
// BeamTuning.TrySet, so geometry knobs still bump GeometryGeneration and the renderer rebuilds the mesh.
//
// Sliders are integer-only, so each knob maps its [Min, Max] float range onto 0..Steps ticks; the resting
// text shows the real float value. Two columns keep the whole set on screen without a scrollbar.
public sealed class GuiDialogBeamTuning : GuiDialog
{
    private const string HotkeyCode = "seamarkstunepanel";
    private const int Steps = 100;         // slider resolution per knob
    private const double RowH = 30;
    private const double LabelW = 118;
    private const double SliderW = 150;
    private const double ColW = LabelW + 8 + SliderW;
    private const double ColGap = 30;

    private readonly string[] keys;

    public GuiDialogBeamTuning(ICoreClientAPI capi) : base(capi)
    {
        keys = BeamTuning.KnobKeys.ToArray();

        capi.Input.RegisterHotKey(HotkeyCode, "Seamarks: beam tuning panel", GlKeys.B, HotkeyType.GUIOrOtherControls, ctrlPressed: true);
        capi.Input.SetHotKeyHandler(HotkeyCode, _ => { Toggle(); return true; });
    }

    public override string ToggleKeyCombinationCode => HotkeyCode;

    // A live shader/geometry tuning panel has no business in survival, so gate it the same way the
    // engine gates its own creative-only tools.
    public override bool TryOpen()
    {
        if (capi.World.Player.WorldData.CurrentGameMode != EnumGameMode.Creative) return false;

        Compose(); // rebuild so sliders reflect any values changed via the command since last open
        return base.TryOpen();
    }

    private void Compose()
    {
        int half = (keys.Length + 1) / 2;

        double gridH = half * RowH;

        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.RightMiddle)
            .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);

        var composer = capi.Gui.CreateCompo("seamarksbeamtuning", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar("Seamarks Beam Tuning", OnTitleBarClose)
            .BeginChildElements(bgBounds);

        double top = 30; // below the title bar
        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            BeamTuning.TryGet(key, out float value, out float min, out float max);

            int col = i < half ? 0 : 1;
            int rowInCol = i < half ? i : i - half;
            double x = col * (ColW + ColGap);
            double y = top + rowInCol * RowH;

            var labelBounds = ElementBounds.Fixed(x, y + 3, LabelW, 22);
            var sliderBounds = ElementStdBounds.Slider(x + LabelW + 8, y, SliderW);

            var slider = new GuiElementSlider(capi, MakeHandler(key, min, max), sliderBounds)
            {
                ShowTextWhenResting = true,
                OnSliderRestingText = cur => FloatFor(cur, min, max).ToString("0.###"),
            };

            composer
                .AddStaticText(key, CairoFont.WhiteSmallText(), labelBounds)
                .AddInteractiveElement(slider, "slider-" + key);

            slider.SetValues(ToTick(value, min, max), 0, Steps, 1);
        }

        double btnY = top + gridH + 8;
        // The 11 shape sliders edit whichever optic shape this toggle selects, and the rest are the shared
        // globals. Toggling re-composes so every slider resyncs to the now-active shape.
        string editLabel = BeamTuning.EditingFocused ? "Editing: WITH LENS" : "Editing: NO LENS";
        composer
            .AddSmallButton(editLabel, OnToggleLook, ElementBounds.Fixed(0, btnY, ColW, 24))
            .AddSmallButton("Reset", OnReset, ElementBounds.Fixed(0, btnY + 30, 90, 24))
            .AddSmallButton("Dump to chat", OnDump, ElementBounds.Fixed(100, btnY + 30, 130, 24))
            .EndChildElements();

        SingleComposer = composer.Compose();
    }

    private Vintagestory.API.Common.ActionConsumable<int> MakeHandler(string key, float min, float max)
    {
        return tick =>
        {
            BeamTuning.TrySet(key, FloatFor(tick, min, max), out _);
            return true;
        };
    }

    private static float FloatFor(int tick, float min, float max) => GameMath.Lerp(min, max, tick / (float)Steps);

    private static int ToTick(float value, float min, float max)
    {
        if (max <= min) return 0;
        int t = (int)Math.Round((value - min) / (max - min) * Steps);
        return Math.Clamp(t, 0, Steps);
    }

    private bool OnToggleLook()
    {
        BeamTuning.SetEditingFocused(!BeamTuning.EditingFocused);
        Compose(); // resync every slider + the button label to the now-active shape
        return true;
    }

    private bool OnReset()
    {
        BeamTuning.Reset();
        Compose(); // resync every slider to the restored defaults
        return true;
    }

    private bool OnDump()
    {
        capi.ShowChatMessage(BeamTuning.Dump());
        return true;
    }

    private void OnTitleBarClose() => TryClose();
}
