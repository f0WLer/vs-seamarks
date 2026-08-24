using Seamarks.Beacon;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Seamarks.Beacon.Client;

// The fitting screen: three lens slots in a row, labelled with the beam each one drives. A panel in any
// slot counts, and its beam number comes from its position among the filled slots, so a gap still numbers
// the filled ones 1, 2 and not 1, 3. The whole layout recomposes on any slot change, so the labels are
// always static text drawn against the current contents.
public class GuiDialogOpticsHousing : GuiDialogBlockEntity
{
    private const int ColStride = 120;
    private const int SlotSize = 50;

    // Layout stops, top to bottom: a gap under the title bar, then the "Beam N" label, the slot, and the
    // name. The spec line sits below the name and moves with it, so a name that wraps to two lines pushes
    // the spec down instead of colliding with it.
    private const int TopPad = 12;
    private const int BeamLabelY = TopPad;
    private const int SlotY = BeamLabelY + 24;
    private const int NameY = SlotY + SlotSize + 6;
    private const int SpecGap = 4;
    private const int LineBounds = 22;
    private const int NoteLineBounds = 14;

    private readonly BlockEntityOpticsHousing be;

    protected override double FloatyDialogPosition => 0.6;

    public GuiDialogOpticsHousing(string title, InventoryBase inventory, BlockPos pos, ICoreClientAPI capi, BlockEntityOpticsHousing be)
        : base(title, inventory, pos, capi)
    {
        this.be = be;
        if (!IsDuplicate)
        {
            capi.World.Player.InventoryManager.OpenInventory(inventory);
            SetupDialog();
        }
    }

    private void OnInventorySlotModified(int slotid)
    {
        capi.Event.EnqueueMainThreadTask(SetupDialog, "setupopticshousingdlg");
    }

    private void SetupDialog()
    {
        int columns = Inventory.Count;

        var label = CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center);
        var noteFont = CairoFont.WhiteDetailText().WithOrientation(EnumTextOrientation.Center);

        // Reserve room for the tallest name across all slots, so every spec line sits at the same height
        // and the row stays level even when one panel's name wraps and another's doesn't.
        var textUtil = new TextDrawUtil();
        double scale = RuntimeEnv.GUIScale;
        double lineHeight = textUtil.GetLineHeight(label) / scale;
        double maxNameHeight = lineHeight;
        for (int i = 0; i < columns; i++)
        {
            if (be.SlotBeamAt(i) is { } b)
            {
                double h = textUtil.GetMultilineTextHeight(label, b.Name, ColStride * scale) / scale;
                maxNameHeight = Math.Max(maxNameHeight, h);
            }
        }

        double specY = NameY + maxNameHeight + SpecGap;
        double noteY = specY + LineBounds;
        double contentHeight = noteY + NoteLineBounds + TopPad;

        var content = ElementBounds.Fixed(0, 0, columns * ColStride, contentHeight);
        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        bgBounds.WithChildren(content);

        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

        ClearComposers();
        var composer = capi.Gui.CreateCompo("seamarksopticshousing" + BlockEntityPosition, dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(DialogTitle, OnTitleBarClose)
            .BeginChildElements(bgBounds);

        for (int i = 0; i < columns; i++)
        {
            int x = i * ColStride;
            var beamBounds = ElementBounds.Fixed(x, BeamLabelY, ColStride, LineBounds);
            var slotBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, x + (ColStride - SlotSize) / 2.0, SlotY, 1, 1);
            var nameBounds = ElementBounds.Fixed(x, NameY, ColStride, maxNameHeight);
            var specBounds = ElementBounds.Fixed(x, specY, ColStride, LineBounds);
            var noteBounds = ElementBounds.Fixed(x, noteY, ColStride, NoteLineBounds);

            BlockEntityOpticsHousing.SlotBeam? beam = be.SlotBeamAt(i);
            if (beam is { } b)
            {
                var nameFont = CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center)
                    .WithColor([b.R, b.G, b.B, 1.0]);
                composer
                    .AddStaticText(Lang.Get("seamarks:gui-housing-beam", b.BeamNumber), label, beamBounds)
                    .AddStaticText(b.Name, nameFont, nameBounds)
                    .AddStaticText(Lang.Get("seamarks:gui-housing-beamspec", b.WidthDeg, b.FlashSeconds), label, specBounds)
                    .AddStaticText(Lang.Get("seamarks:gui-housing-beamspec-note"), noteFont, noteBounds);
            }
            else
            {
                composer.AddStaticText(Lang.Get("seamarks:gui-housing-nolens"), label, nameBounds);
            }

            composer.AddItemSlotGrid(Inventory, SendInvPacket, 1, [i], slotBounds, "slot" + i);
        }

        SingleComposer = composer.EndChildElements().Compose();
    }

    private void SendInvPacket(object packet)
    {
        capi.Network.SendBlockEntityPacket(BlockEntityPosition.X, BlockEntityPosition.Y, BlockEntityPosition.Z, packet);
    }

    private void OnTitleBarClose() => TryClose();

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        Inventory.SlotModified += OnInventorySlotModified;
    }

    public override void OnGuiClosed()
    {
        Inventory.SlotModified -= OnInventorySlotModified;
        for (int i = 0; i < Inventory.Count; i++)
        {
            SingleComposer.GetSlotGrid("slot" + i)?.OnGuiClosed(capi);
        }
        base.OnGuiClosed();
    }
}
