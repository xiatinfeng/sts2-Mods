using Godot;
using System;
using System.Collections.Generic;

namespace MapOddsTracker.Scripts.UI;

/// <summary>
/// A tooltip that displays monster name and image(s) loaded from local bundled assets.
/// Supports multi-monster encounters by showing images side-by-side.
/// </summary>
public partial class MonsterTooltip : PanelContainer
{
    private Label _nameLabel = null!;
    private HBoxContainer _imageRow = null!;
    private readonly List<TextureRect> _imageRects = new();

    // Simple texture cache to avoid reloading
    private static readonly Dictionary<string, Texture2D?> TextureCache = new();

    private const float SingleImageSize = 120f;
    private const float MultiImageSize = 80f;

    public override void _Ready()
    {
        // Semi-transparent dark background
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.05f, 0.08f, 0.95f),
            BorderColor = new Color(0.3f, 0.3f, 0.4f, 0.8f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 8,
            ContentMarginTop = 8,
            ContentMarginRight = 8,
            ContentMarginBottom = 8
        };
        AddThemeStyleboxOverride("panel", style);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);
        AddChild(vbox);

        // Monster name label
        _nameLabel = new Label();
        _nameLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _nameLabel.AddThemeFontSizeOverride("font_size", 13);
        _nameLabel.Modulate = new Color(1f, 0.9f, 0.7f);
        vbox.AddChild(_nameLabel);

        // Image row (HBox for multi-monster support)
        _imageRow = new HBoxContainer();
        _imageRow.AddThemeConstantOverride("separation", 4);
        _imageRow.Alignment = BoxContainer.AlignmentMode.Center;
        vbox.AddChild(_imageRow);

        // Default hidden
        Hide();
    }

    /// <summary>
    /// Show tooltip with monster info at specified position.
    /// </summary>
    public void ShowMonster(string displayName, string englishId, List<string> monsterIds, Vector2 position, bool isBoss = false)
    {
        _nameLabel.Text = displayName;

        // Determine which IDs to display
        var idsToShow = new List<string>();
        if (monsterIds.Count > 0)
        {
            idsToShow.AddRange(monsterIds);
        }
        else
        {
            idsToShow.Add(englishId);
        }

        GD.Print($"[MapOddsTracker] ShowMonster: displayName={displayName}, ids=[{string.Join(", ", idsToShow)}], isBoss={isBoss}");

        // Clear previous images
        foreach (var child in _imageRow.GetChildren())
            child.QueueFree();
        _imageRects.Clear();

        bool anyLoaded = false;
        bool isMulti = idsToShow.Count > 1;
        float imgSize = isMulti ? MultiImageSize : SingleImageSize;

        foreach (var id in idsToShow)
        {
            var imagePath = MonsterImageMapper.GetImagePath(id, isBoss);
            GD.Print($"[MapOddsTracker] Looking up image for '{id}' (isBoss={isBoss}): {(imagePath ?? "NOT FOUND")}");
            
            var imgRect = new TextureRect();
            imgRect.ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional;
            imgRect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
            imgRect.CustomMinimumSize = new Vector2(imgSize, imgSize);
            _imageRow.AddChild(imgRect);
            _imageRects.Add(imgRect);

            if (imagePath != null)
            {
                bool loaded = LoadImageIntoRect(imagePath, imgRect);
                if (loaded) anyLoaded = true;
            }
            else
            {
                imgRect.Texture = null;
            }
        }

        if (!anyLoaded)
        {
            _nameLabel.Text = $"{displayName}\n(无图片)";
            GD.Print($"[MapOddsTracker] No images loaded for '{displayName}'");
        }

        // Calculate expected tooltip size for boundary detection
        int monsterCount = idsToShow.Count;
        float contentWidth = monsterCount == 1 ? imgSize : monsterCount * imgSize + (monsterCount - 1) * 4;
        float contentHeight = imgSize + 26; // image + approx label height + vbox separation
        float marginH = 16; // ContentMarginLeft + Right = 8 + 8
        float marginV = 16; // ContentMarginTop + Bottom = 8 + 8
        float tooltipWidth = contentWidth + marginH;
        float tooltipHeight = contentHeight + marginV;

        // Boundary-aware positioning
        var viewport = GetViewport();
        if (viewport != null)
        {
            var screenSize = viewport.GetVisibleRect().Size;
            Vector2 finalPos = position;

            // Horizontal: show to right by default, flip to left if overflowing
            if (position.X + tooltipWidth + 15 > screenSize.X)
            {
                finalPos.X = position.X - tooltipWidth - 10;
            }
            else
            {
                finalPos.X = position.X + 15;
            }

            // Vertical: show below by default, flip to above if overflowing
            if (position.Y + tooltipHeight + 15 > screenSize.Y)
            {
                finalPos.Y = position.Y - tooltipHeight - 10;
            }
            else
            {
                finalPos.Y = position.Y + 15;
            }

            // Clamp to top-left edge with padding
            finalPos.X = Mathf.Max(10, finalPos.X);
            finalPos.Y = Mathf.Max(10, finalPos.Y);

            GlobalPosition = finalPos;
        }
        else
        {
            GlobalPosition = position + new Vector2(15, 15);
        }

        Show();
        ZIndex = 100;
    }

    public void HideTooltip()
    {
        Hide();
        foreach (var rect in _imageRects)
            rect.Texture = null;
        _imageRects.Clear();
    }

    private bool LoadImageIntoRect(string absolutePath, TextureRect imgRect)
    {
        // Check cache first
        if (TextureCache.TryGetValue(absolutePath, out var cachedTexture))
        {
            imgRect.Texture = cachedTexture;
            GD.Print($"[MapOddsTracker] Image loaded from cache: {absolutePath}");
            return cachedTexture != null;
        }

        try
        {
            var image = new Image();
            var error = image.Load(absolutePath);

            if (error == Error.Ok)
            {
                var texture = ImageTexture.CreateFromImage(image);
                imgRect.Texture = texture;
                TextureCache[absolutePath] = texture;
                GD.Print($"[MapOddsTracker] Image loaded successfully: {absolutePath}");
                return true;
            }
            else
            {
                GD.PrintErr($"[MapOddsTracker] Failed to load image '{absolutePath}': {error}");
                imgRect.Texture = null;
                TextureCache[absolutePath] = null;
                return false;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MapOddsTracker] Image loading exception for '{absolutePath}': {ex.Message}");
            imgRect.Texture = null;
            TextureCache[absolutePath] = null;
            return false;
        }
    }

    private void ClampToScreen()
    {
        var viewport = GetViewport();
        if (viewport == null) return;

        var screenSize = viewport.GetVisibleRect().Size;
        var rect = GetGlobalRect();
        Vector2 pos = GlobalPosition;

        if (rect.Position.X + rect.Size.X > screenSize.X)
        {
            pos.X = screenSize.X - rect.Size.X - 10;
        }
        if (rect.Position.X < 0)
        {
            pos.X = 10;
        }

        if (rect.Position.Y + rect.Size.Y > screenSize.Y)
        {
            pos.Y = screenSize.Y - rect.Size.Y - 10;
        }
        if (rect.Position.Y < 0)
        {
            pos.Y = 10;
        }

        GlobalPosition = pos;
    }
}
