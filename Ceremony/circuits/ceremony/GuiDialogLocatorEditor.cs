using System;
using System.Linq;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

#nullable disable

namespace circuits
{
    public class GuiDialogLocatorEditor : GuiDialogGeneric
    {
        private readonly Action<CustomLocatorProps, string> onConfirm;
        private readonly CustomLocatorProps props;

        private int[] colors;
        private string[] iconDrawKeys;
        private string[] iconSaveValues;

        private int selectedColor;
        private string selectedIcon;
        private string selectedVariantType;

        private readonly string[] variantTypes =
        {
            "map-blank",
            "map1",
            "map2",
            "map-cavetobias",
            "map-devastationarea",
            "map-treasures"
        };

        public override string ToggleKeyCombinationCode => null;

        public GuiDialogLocatorEditor(
            ICoreClientAPI capi,
            WaypointMapLayer wml,
            CustomLocatorProps initial,
            string currentVariantType,
            Action<CustomLocatorProps, string> onConfirm
        ) : base("", capi)
        {
            this.onConfirm = onConfirm;

            Vec3i initialPos = initial?.WaypointPos == null
                ? new Vec3i(0, 0, 0)
                : new Vec3i(initial.WaypointPos.X, initial.WaypointPos.Y, initial.WaypointPos.Z);

            props = new CustomLocatorProps
            {
                WaypointText = initial?.WaypointText ?? "",
                WaypointIcon = initial?.WaypointIcon ?? "",
                WaypointColorSwatch = EnsureOpaque(initial?.WaypointColorSwatch ?? 15727967),
                WaypointPos = initialPos,
                IsWritten = initial?.IsWritten ?? false
            };

            BuildWaypointIcons(wml);

            colors = wml?.WaypointColors != null
                ? wml.WaypointColors
                    .Select(EnsureOpaque)
                    .Distinct()
                    .ToArray()
                : Array.Empty<int>();

            if (colors.Length == 0)
            {
                colors = new[] { props.WaypointColorSwatch };
            }

            selectedIcon = iconSaveValues.Contains(props.WaypointIcon)
                ? props.WaypointIcon
                : iconSaveValues.FirstOrDefault() ?? "";

            selectedColor = props.WaypointColorSwatch;

            selectedVariantType = variantTypes.Contains(currentVariantType)
                ? currentVariantType
                : "map-treasures";

            int colorIndex = Array.FindIndex(
                colors,
                color => SameRgb(color, props.WaypointColorSwatch)
            );

            if (colorIndex < 0)
            {
                colors = colors.Append(props.WaypointColorSwatch).ToArray();
                colorIndex = colors.Length - 1;
            }

            int iconIndex = Array.IndexOf(iconSaveValues, selectedIcon);
            if (iconIndex < 0) iconIndex = 0;

            ComposeDialog(colorIndex, iconIndex);
        }

        public override bool TryOpen()
        {
            return base.TryOpen();
        }

        private static int EnsureOpaque(int color)
        {
            return color | unchecked((int)0xFF000000);
        }

        private static bool SameRgb(int a, int b)
        {
            return (a & 0x00FFFFFF) == (b & 0x00FFFFFF);
        }

        private void BuildWaypointIcons(WaypointMapLayer wml)
        {
            if (wml?.WaypointIcons == null)
            {
                iconDrawKeys = Array.Empty<string>();
                iconSaveValues = Array.Empty<string>();
                return;
            }

            IconEntry[] entries = wml.WaypointIcons.Keys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(TryMakeIconEntry)
                .Where(entry => entry != null)
                .GroupBy(entry => entry.SaveValue)
                .Select(group => group.First())
                .OrderBy(entry => entry.SaveValue)
                .ToArray();

            iconDrawKeys = entries.Select(entry => entry.DrawKey).ToArray();
            iconSaveValues = entries.Select(entry => entry.SaveValue).ToArray();
        }

        private IconEntry TryMakeIconEntry(string key)
        {
            // Case 1: key is already the draw key, e.g. "wpCircle".
            if (key.StartsWith("wp", StringComparison.OrdinalIgnoreCase) && CanDrawIcon(key))
            {
                return new IconEntry
                {
                    DrawKey = key,
                    SaveValue = ToWaypointSaveValue(key)
                };
            }

            // Case 2: key is a bare waypoint value, e.g. "circle".
            string wpKey = "wp" + key.UcFirst();
            if (CanDrawIcon(wpKey))
            {
                return new IconEntry
                {
                    DrawKey = wpKey,
                    SaveValue = key
                };
            }

            // Case 3: modded direct icon key that is not wp-prefixed.
            if (CanDrawIcon(key))
            {
                return new IconEntry
                {
                    DrawKey = key,
                    SaveValue = key
                };
            }

            capi.Logger.Warning("[ceremony] Skipping broken waypoint icon key '{0}'", key);
            return null;
        }

        private bool CanDrawIcon(string drawKey)
        {
            try
            {
                using ImageSurface surface = new ImageSurface(Format.Argb32, 32, 32);
                using Context ctx = new Context(surface);

                capi.Gui.Icons.DrawIcon(
                    ctx,
                    drawKey,
                    0,
                    0,
                    24,
                    24,
                    new double[] { 1, 1, 1, 1 }
                );

                return true;
            }
            catch
            {
                return false;
            }
        }

        private string ToWaypointSaveValue(string drawKey)
        {
            if (drawKey.StartsWith("wp", StringComparison.OrdinalIgnoreCase) && drawKey.Length > 2)
            {
                string bare = drawKey.Substring(2);
                return char.ToLowerInvariant(bare[0]) + bare.Substring(1);
            }

            return drawKey;
        }

        private void ComposeDialog(int colorIndex, int iconIndex)
        {
            ElementBounds left = ElementBounds.Fixed(0, 28, 120, 25);
            ElementBounds right = left.RightCopy();
            ElementBounds buttonRow = ElementBounds.Fixed(0, 28, 360, 25);

            ElementBounds bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bg.BothSizing = ElementSizing.FitToChildren;

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle)
                .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);

            SingleComposer?.Dispose();

            int iconSize = 22;

            int variantIndex = variantTypes.IndexOf(selectedVariantType);
            if (variantIndex < 0) variantIndex = 0;

            GuiComposer composer = capi.Gui.CreateCompo("customlocator-wp", dialogBounds)
                .AddShadedDialogBG(bg, withTitleBar: false)
                .AddDialogTitleBar(Lang.Get("Create Locator Map"), () => { TryClose(); })
                .BeginChildElements(bg)

                .AddStaticText(
                    Lang.Get("Waypoint name"),
                    CairoFont.WhiteSmallText(),
                    left = left.FlatCopy()
                )
                .AddTextInput(
                    right = right.FlatCopy().WithFixedWidth(200),
                    OnNameChanged,
                    CairoFont.TextInput(),
                    "nameInput"
                )

                .AddStaticText(
                    "Map Variant",
                    CairoFont.WhiteSmallText(),
                    left = left.BelowCopy(0, 9)
                )
                .AddDropDown(
                    variantTypes,
                    variantTypes,
                    variantIndex,
                    OnVariantSelected,
                    right = right.BelowCopy(0, 5).WithFixedWidth(200),
                    "variantDropdown"
                )

                .AddRichtext(
                    Lang.Get("waypoint-color"),
                    CairoFont.WhiteSmallText(),
                    left = left.BelowCopy(0, 5)
                )
                .AddColorListPicker(
                    colors,
                    OnColorSelected,
                    left = left.BelowCopy(0, 5).WithFixedSize(iconSize, iconSize),
                    270,
                    "colorpicker"
                )

                .AddStaticText(
                    Lang.Get("Icon"),
                    CairoFont.WhiteSmallText(),
                    left = left
                        .WithFixedPosition(0, left.fixedY + left.fixedHeight)
                        .WithFixedWidth(100)
                        .BelowCopy()
                );

            if (iconDrawKeys.Length > 0)
            {
                composer.AddDirectIconListPicker(
                    iconDrawKeys,
                    OnIconSelected,
                    left = left.BelowCopy(0, 5).WithFixedSize(iconSize + 5, iconSize + 5),
                    270,
                    "iconpicker"
                );

                int iconsPerRow = Math.Max(1, (int)(270 / (iconSize + 5 + 4)));
                int iconRows = (int)Math.Ceiling(iconDrawKeys.Length / (double)iconsPerRow);

                left = left.FlatCopy().WithFixedHeight(iconRows * (iconSize + 5 + 4));
            }
            else
            {
                left = left.BelowCopy(0, 5);

                composer.AddStaticText(
                    "No valid waypoint icons found",
                    CairoFont.WhiteSmallText(),
                    left.WithFixedWidth(270)
                );
            }

            SingleComposer = composer
                .AddSmallButton(
                    Lang.Get("Cancel"),
                    OnCancel,
                    buttonRow.FlatCopy().FixedUnder(left).WithFixedWidth(100)
                )
                .AddSmallButton(
                    Lang.Get("Save"),
                    OnSave,
                    buttonRow.FlatCopy()
                        .FixedUnder(left)
                        .WithFixedWidth(100)
                        .WithAlignment(EnumDialogArea.RightFixed),
                    EnumButtonStyle.Normal,
                    "saveButton"
                )

                .EndChildElements()
                .Compose();

            SingleComposer.ColorListPickerSetValue(
                "colorpicker",
                Math.Clamp(colorIndex, 0, colors.Length - 1)
            );

            if (iconDrawKeys.Length > 0)
            {
                SingleComposer.DirectIconListPickerSetValue(
                    "iconpicker",
                    Math.Clamp(iconIndex, 0, iconDrawKeys.Length - 1)
                );
            }

            SingleComposer.GetTextInput("nameInput").SetValue(props.WaypointText ?? "");

            var saveButton = SingleComposer.GetButton("saveButton");
            if (saveButton != null)
            {
                saveButton.Enabled = !string.IsNullOrWhiteSpace(props.WaypointText);
            }

            SingleComposer.GetDropDown("variantDropdown")?.SetSelectedValue(selectedVariantType);
        }

        private void OnColorSelected(int index)
        {
            if (index < 0 || index >= colors.Length) return;

            selectedColor = EnsureOpaque(colors[index]);

            SingleComposer?.ColorListPickerSetValue("colorpicker", index);
        }

        private void OnIconSelected(int index)
        {
            if (index < 0 || index >= iconSaveValues.Length) return;

            selectedIcon = iconSaveValues[index];

            SingleComposer?.DirectIconListPickerSetValue("iconpicker", index);
        }

        private bool OnSave()
        {
            string name = SingleComposer.GetTextInput("nameInput").GetText() ?? "";

            props.WaypointText = name;
            props.WaypointIcon = selectedIcon ?? "";
            props.WaypointColorSwatch = EnsureOpaque(selectedColor);

            onConfirm(props, selectedVariantType);

            TryClose();
            return true;
        }

        private bool OnCancel()
        {
            TryClose();
            return true;
        }

        private void OnVariantSelected(string code, bool selected)
        {
            if (!selected) return;

            selectedVariantType = code;
        }

        private void OnNameChanged(string text)
        {
            var saveButton = SingleComposer.GetButton("saveButton");
            if (saveButton != null)
            {
                saveButton.Enabled = !string.IsNullOrWhiteSpace(text);
            }
        }

        private sealed class IconEntry
        {
            public string DrawKey;
            public string SaveValue;
        }
    }

    public class GuiElementDirectIconListPicker : GuiElementElementListPickerBase<string>
    {
        public GuiElementDirectIconListPicker(ICoreClientAPI capi, string iconCode, ElementBounds bounds)
            : base(capi, iconCode, bounds)
        {
        }

        public override void DrawElement(string iconCode, Context ctx, ImageSurface surface)
        {
            ctx.SetSourceRGBA(1, 1, 1, 0.2);
            RoundRectangle(ctx, Bounds.drawX, Bounds.drawY, Bounds.InnerWidth, Bounds.InnerHeight, 1);
            ctx.Fill();

            try
            {
                api.Gui.Icons.DrawIcon(
                    ctx,
                    iconCode,
                    Bounds.drawX + 2,
                    Bounds.drawY + 2,
                    Bounds.InnerWidth - 4,
                    Bounds.InnerHeight - 4,
                    new double[] { 1, 1, 1, 1 }
                );
            }
            catch
            {
                ctx.SetSourceRGBA(1, 0.25, 0.25, 1);
                ctx.LineWidth = 2;

                ctx.MoveTo(Bounds.drawX + 5, Bounds.drawY + 5);
                ctx.LineTo(Bounds.drawX + Bounds.InnerWidth - 5, Bounds.drawY + Bounds.InnerHeight - 5);

                ctx.MoveTo(Bounds.drawX + Bounds.InnerWidth - 5, Bounds.drawY + 5);
                ctx.LineTo(Bounds.drawX + 5, Bounds.drawY + Bounds.InnerHeight - 5);

                ctx.Stroke();
            }
        }
    }

    public static class DirectIconListPickerComposerHelpers
    {
        public static GuiElementDirectIconListPicker GetDirectIconListPicker(
            this GuiComposer composer,
            string key
        )
        {
            return composer.GetElement(key) as GuiElementDirectIconListPicker;
        }

        public static void DirectIconListPickerSetValue(
            this GuiComposer composer,
            string key,
            int selectedIndex
        )
        {
            int i = 0;
            GuiElementDirectIconListPicker btn;

            while ((btn = composer.GetDirectIconListPicker(key + "-" + i)) != null)
            {
                btn.SetValue(i == selectedIndex);
                i++;
            }
        }

        public static GuiComposer AddDirectIconListPicker(
            this GuiComposer composer,
            string[] iconCodes,
            Action<int> onToggle,
            ElementBounds startBounds,
            int maxLineWidth,
            string key = null
        )
        {
            if (iconCodes == null) iconCodes = Array.Empty<string>();

            double x = 0;
            double y = 0;
            double spacing = 4;

            for (int i = 0; i < iconCodes.Length; i++)
            {
                if (x > 0 && x + startBounds.fixedWidth > maxLineWidth)
                {
                    x = 0;
                    y += startBounds.fixedHeight + spacing;
                }

                ElementBounds bounds = startBounds
                    .FlatCopy()
                    .WithFixedPosition(startBounds.fixedX + x, startBounds.fixedY + y);

                GuiElementDirectIconListPicker elem = new GuiElementDirectIconListPicker(
                    composer.Api,
                    iconCodes[i],
                    bounds
                );

                int index = i;

                elem.handler = on =>
                {
                    onToggle?.Invoke(index);
                };

                composer.AddInteractiveElement(elem, key == null ? null : key + "-" + i);

                x += startBounds.fixedWidth + spacing;
            }

            return composer;
        }
    }
}