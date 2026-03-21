# Umbra XIV Integration Guide
# Building Toolbar Widgets and World Markers with Una.Drawing

Author: Sansflaire
Last Updated: 2026-03-19
Umbra Version: 3.1.12.0 (API Level 14)

---

## Overview

Umbra XIV is a modular toolbar plugin for FFXIV with 880K+ downloads that renders its UI using
**Una.Drawing** — a custom node-tree system backed by **SkiaSharp** (the same engine as Chrome,
Android, and Flutter). This gives Umbra widgets rounded corners, drop shadows, animations,
gradient fills, blend modes, and typographic control that vanilla ImGui cannot match.

Other plugins can extend Umbra by shipping a second DLL that Umbra loads at runtime. This is
the recommended path to get polished Umbra-quality UI in your own plugin.

**What you can build:**
- **Toolbar Widgets** — buttons, text, icons, progress bars that live in the Umbra toolbar
- **World Markers** — 3D labels and icons rendered in the game world

**Umbra does NOT replace ImGui windows.** Standalone floating windows still require ImGui.
Umbra integration is specifically for toolbar-embedded UI and world-space markers.

---

## Ecosystem Repositories

| Repo | Purpose |
|------|---------|
| https://github.com/una-xiv/umbra | Main Umbra plugin source |
| https://github.com/una-xiv/Umbra.Common | DI framework (Umbra.Common.dll) |
| https://github.com/una-xiv/drawing | Una.Drawing library source |
| https://github.com/una-xiv/Umbra.SamplePlugin | Reference implementation (start here) |
| https://github.com/una-xiv/Umbra.Glamourer | Real-world integration example |
| https://una-xiv.github.io/umbra-docs/ | Official documentation site |

---

## Architecture

```
FFXIV Process
└── Dalamud
    └── Umbra XIV Plugin (Umbra.dll)
        ├── Una.Drawing (SkiaSharp renderer)
        ├── Umbra.Common (DI container + lifecycle)
        ├── Umbra.Game (FFXIV game APIs)
        └── [Your Plugin DLL] ← loaded by Umbra at runtime
            ├── SampleWidget.cs  (IToolbarWidget)
            └── SampleMarker.cs  (WorldMarkerFactory)
```

Your DLL is a **separate Dalamud plugin** that references Umbra's assemblies. Umbra discovers
and loads it through Dalamud's plugin system. You do NOT replace or modify Umbra itself.

---

## Project Setup

### Required Assembly References

All Umbra assemblies are in the installed plugin folder. Use a property to locate them:

```xml
<PropertyGroup>
  <UmbraLibPath>$(APPDATA)\XIVLauncher\installedPlugins\Umbra\3.1.12.0\</UmbraLibPath>
  <DalamudLibPath>$(APPDATA)\XIVLauncher\addon\Hooks\dev\</DalamudLibPath>
</PropertyGroup>
```

> NOTE: The version folder `3.1.12.0` will change when Umbra updates. You may want to use
> `$([System.IO.Directory]::GetDirectories('...')[0])` to grab the first version folder
> dynamically, or just update the path manually.

### Full .csproj Template

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Platforms>x64</Platforms>
    <Platform>x64</Platform>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <ProduceReferenceAssembly>false</ProduceReferenceAssembly>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <AssemblyName>MyUmbraPlugin</AssemblyName>
    <RootNamespace>MyUmbraPlugin</RootNamespace>
    <Version>0.1.0</Version>
  </PropertyGroup>

  <PropertyGroup>
    <UmbraLibPath>$(APPDATA)\XIVLauncher\installedPlugins\Umbra\3.1.12.0\</UmbraLibPath>
    <DalamudLibPath>$(APPDATA)\XIVLauncher\addon\Hooks\dev\</DalamudLibPath>
  </PropertyGroup>

  <ItemGroup>
    <!-- Dalamud (required) -->
    <Reference Include="Dalamud"               HintPath="$(DalamudLibPath)Dalamud.dll"               Private="false" />
    <Reference Include="Dalamud.Common"        HintPath="$(DalamudLibPath)Dalamud.Common.dll"        Private="false" />
    <Reference Include="Dalamud.Bindings.ImGui" HintPath="$(DalamudLibPath)Dalamud.Bindings.ImGui.dll" Private="false" />
    <Reference Include="FFXIVClientStructs"    HintPath="$(DalamudLibPath)FFXIVClientStructs.dll"    Private="false" />
    <Reference Include="Lumina"               HintPath="$(DalamudLibPath)Lumina.dll"               Private="false" />
    <Reference Include="Lumina.Excel"         HintPath="$(DalamudLibPath)Lumina.Excel.dll"         Private="false" />
    <Reference Include="Newtonsoft.Json"      HintPath="$(DalamudLibPath)Newtonsoft.Json.dll"      Private="false" />

    <!-- Umbra (required) -->
    <Reference Include="Umbra"        HintPath="$(UmbraLibPath)Umbra.dll"        Private="false" />
    <Reference Include="Umbra.Common" HintPath="$(UmbraLibPath)Umbra.Common.dll" Private="false" />

    <!-- Umbra (optional) -->
    <Reference Include="Umbra.Game"   HintPath="$(UmbraLibPath)Umbra.Game.dll"   Private="false" />
    <Reference Include="Una.Drawing"  HintPath="$(UmbraLibPath)Una.Drawing.dll"  Private="false" />
  </ItemGroup>

  <Target Name="CopyToDevPlugins" AfterTargets="Build">
    <Copy
      SourceFiles="$(TargetPath)"
      DestinationFolder="$(APPDATA)\XIVLauncher\devPlugins\MyUmbraPlugin\"
      OverwriteReadOnlyFiles="true"
    />
  </Target>
</Project>
```

All Umbra refs use `Private="false"` — Umbra already ships these DLLs and Dalamud/Umbra will
provide them at runtime. Do NOT copy them to your output folder.

---

## Toolbar Widgets

### Class Hierarchy

```
ToolbarWidget (abstract)        ← defined in Umbra.dll
  └── StandardToolbarWidget     ← concrete helper class you extend
        └── YourWidget          ← your implementation
```

`StandardToolbarWidget` gives you pre-wired text, sub-text, icon, and progress bar slots.
Extend it unless you need fully custom node layout, in which case extend `ToolbarWidget` directly.

### Registration Attribute

```csharp
[ToolbarWidget(
    id:          "MyPlugin.MyWidget",   // globally unique — use "PluginName.WidgetName"
    name:        "My Widget",           // shown in Umbra widget picker
    description: "Does something cool", // shown in widget picker
    tags:        new[] { "utility" }    // searchable tags
)]
public sealed class MyWidget : StandardToolbarWidget { ... }
```

Umbra discovers all classes with `[ToolbarWidget]` in loaded assemblies automatically.

### StandardToolbarWidget Features

Declare which built-in slots your widget uses in your constructor or `OnLoad()`:

```csharp
// Feature flags — combine with |
protected StandardWidgetFeatures Features { get; set; }

// Available flags:
// StandardWidgetFeatures.Text          — left label
// StandardWidgetFeatures.SubText       — right secondary label
// StandardWidgetFeatures.Icon          — icon slot (game icon or FA icon)
// StandardWidgetFeatures.CustomIconSize — allow user to resize icon
// StandardWidgetFeatures.ProgressBar   — horizontal progress bar underlay
```

Setter methods (call from `OnDraw()` to update each frame):

```csharp
protected void SetText(string text);
protected void SetSubText(string text);
protected void SetGameIconId(uint iconId);        // game texture icon by ID
protected void SetFontAwesomeIcon(string icon);  // Font Awesome icon glyph name
protected void SetGameGlyphIcon(char glyph);     // game glyph font character
protected void SetProgressBarValue(float value); // 0.0–1.0
protected void SetProgressBarConstraint(uint pixelSize);
```

### Lifecycle Methods

```csharp
protected override void OnLoad()
{
    // Called when widget is added to the toolbar.
    // Set up Features flags, subscribe to events, init state.
    Features = StandardWidgetFeatures.Text | StandardWidgetFeatures.Icon;
}

protected override void OnUnload()
{
    // Called when widget is removed or plugin unloads.
    // Unsubscribe events, release resources.
}

protected override void OnDraw()
{
    // Called every frame while widget is visible.
    // Update text/icon/progress values here.
    SetText("Hello");
    SetGameIconId(60071); // example: gil bag icon
}

protected override void OnConfigurationChanged()
{
    // Called when the user changes widget settings in Umbra's config UI.
    // Re-read config variables here.
}

protected override string GetInstanceName()
{
    // Optional: return a dynamic name for this widget instance.
    return "My Widget";
}
```

### Config Variables

Expose user-configurable settings that appear in Umbra's widget config panel:

```csharp
protected override IEnumerable<IWidgetConfigVariable> GetConfigVariables()
{
    // Always yield base variables first (controls text/icon visibility etc.)
    foreach (var v in base.GetConfigVariables()) yield return v;

    // Boolean toggle
    yield return new BooleanWidgetConfigVariable(
        id:           "ShowSubText",
        name:         "Show Sub-Text",
        description:  "Show secondary label",
        defaultValue: true
    );

    // Integer input
    yield return new IntegerWidgetConfigVariable(
        id:           "MaxEntries",
        name:         "Max Entries",
        description:  "Maximum rows to display",
        defaultValue: 5,
        minValue:     1,
        maxValue:     20
    );

    // String input
    yield return new StringWidgetConfigVariable(
        id:           "Label",
        name:         "Label Text",
        description:  "Custom label",
        defaultValue: "DPS"
    );

    // Enum/select dropdown
    yield return new SelectWidgetConfigVariable(
        id:           "Mode",
        name:         "Display Mode",
        description:  "What to show",
        defaultValue: "DPS",
        options:      new[] { "DPS", "HPS", "DTaken" }
    );
}

// Reading config values:
protected override void OnDraw()
{
    bool   showSub = GetConfigValue<bool>("ShowSubText");
    int    max     = GetConfigValue<int>("MaxEntries");
    string label   = GetConfigValue<string>("Label") ?? "DPS";
    string mode    = GetConfigValue<string>("Mode") ?? "DPS";
}
```

### Minimal Complete Widget Example

```csharp
using System.Collections.Generic;
using Umbra.Widgets;
using Umbra.Widgets.System;

namespace DamageMeter.Umbra;

[ToolbarWidget(
    id:          "DamageMeter.DpsWidget",
    name:        "Damage Meter",
    description: "Shows current DPS from the DamageMeter plugin.",
    tags:        new[] { "combat", "dps", "meter" }
)]
public sealed class DpsWidget : StandardToolbarWidget
{
    // Injected by Umbra.Common DI — your own plugin's tracker service
    private CombatTracker? _tracker;

    protected override void OnLoad()
    {
        Features = StandardWidgetFeatures.Text | StandardWidgetFeatures.SubText;
    }

    protected override void OnUnload() { }

    protected override void OnDraw()
    {
        if (_tracker == null) return;

        float dps = _tracker.LocalPlayerDps;
        SetText($"{dps:F0}");
        SetSubText("DPS");
    }

    protected override IEnumerable<IWidgetConfigVariable> GetConfigVariables()
    {
        foreach (var v in base.GetConfigVariables()) yield return v;
        // add custom variables here if needed
    }
}
```

---

## World Markers

World markers render 3D labels and icons in the game world, with automatic distance fading
and optional compass display.

### WorldMarker Record

```csharp
public record WorldMarker
{
    public Guid    Key                 { get; init; }   // unique ID for this marker
    public string  Label               { get; init; }   // main text label
    public string  SubLabel            { get; init; }   // secondary text (e.g. distance)
    public uint    IconId              { get; init; }   // game icon texture ID
    public uint    IconWidth           { get; init; } = 32;
    public uint    IconHeight          { get; init; } = 32;
    public Vector3 Position            { get; init; }   // X, Z, Y (note: Y = elevation)
    public Vector2 FadeDistance        { get; init; }   // (nearFade, farFade) in yalms
    public float   MaxVisibleDistance  { get; init; }   // 0 = always visible
    public uint    MapId               { get; init; }   // territory/zone ID
    public bool    ShowOnCompass       { get; init; }
    public bool    IsVisible           { get; init; } = true;
}
```

### WorldMarkerFactory Base Class

```csharp
public abstract class WorldMarkerFactory
{
    // Identity — must be unique across all plugins
    public abstract string Id          { get; }
    public abstract string Name        { get; }
    public abstract string Description { get; }

    // Config
    public abstract IEnumerable<IWidgetConfigVariable> GetConfigVariables();

    // Marker management
    protected void SetMarker(WorldMarker marker);
    protected void RemoveMarker(WorldMarker marker);
    protected void RemoveMarker(Guid key);
    protected void RemoveAllMarkers();

    // Lifecycle overrides
    protected virtual void OnInitialized()                  { }
    protected virtual void OnZoneChanged(uint zoneId)       { }
    protected virtual void OnConfigUpdated(string key)      { }

    // Config helpers
    protected T?   GetConfigValue<T>(string key);
    protected void SetConfigValue<T>(string key, T value);

    // Pre-built config variable sets
    protected IEnumerable<IWidgetConfigVariable> DefaultStateConfigVariables  { get; }
    protected IEnumerable<IWidgetConfigVariable> DefaultFadeConfigVariables   { get; }
}
```

### Minimal Marker Factory Example

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using Umbra.Markers;
using Umbra.Widgets;

namespace DamageMeter.Umbra;

public sealed class PartyMemberMarkerFactory : WorldMarkerFactory
{
    public override string Id          => "DamageMeter.PartyMarkers";
    public override string Name        => "Party DPS Markers";
    public override string Description => "Shows DPS above party member heads.";

    public override IEnumerable<IWidgetConfigVariable> GetConfigVariables()
    {
        foreach (var v in DefaultStateConfigVariables) yield return v;
        foreach (var v in DefaultFadeConfigVariables)  yield return v;
    }

    protected override void OnZoneChanged(uint zoneId)
    {
        RemoveAllMarkers();
    }

    // Call this from a Framework.Update tick handler with current party data
    public void UpdateMarkers(IEnumerable<(Guid id, string name, float dps, Vector3 pos, uint mapId)> entries)
    {
        RemoveAllMarkers();
        foreach (var (id, name, dps, pos, mapId) in entries)
        {
            SetMarker(new WorldMarker
            {
                Key               = id,
                Label             = $"{dps:F0} DPS",
                SubLabel          = name,
                IconId            = 0,
                Position          = pos,
                FadeDistance      = new Vector2(10f, 30f),
                MaxVisibleDistance = 50f,
                MapId             = mapId,
                ShowOnCompass     = false,
                IsVisible         = true
            });
        }
    }
}
```

---

## Una.Drawing Node System

If you extend `ToolbarWidget` directly (bypassing `StandardToolbarWidget`) you must build your
widget's visual tree using Una.Drawing nodes. This gives you full control over the rendered UI.

### Node Fundamentals

A `Node` is a visual element in a tree. Each node has a `Style`, child nodes, and event hooks.
The renderer walks the tree every frame and draws it with Skia.

```csharp
using Una.Drawing;

// Create a node
var root = new Node
{
    Id        = "root",
    NodeValue = "Hello World",  // text content (for text-rendering nodes)
    Style     = new Style
    {
        Size            = new Size(200, 40),
        BackgroundColor = new Color("#1A1A2E"),
        BorderRadius    = 6f,
        Color           = new Color("#FFFFFF"),
        FontSize        = 13f,
        Padding         = new EdgeSize(4, 8)
    }
};

// Build a tree
var container = new Node { Id = "container" };
container.AppendChild(new Node { Id = "label", NodeValue = "DPS" });
container.AppendChild(new Node { Id = "value", NodeValue = "12,450" });
```

### Hierarchy API

```csharp
node.AppendChild(child);           // add child at end
node.PrependChild(child);          // add child at start
node.RemoveChild(child);           // remove specific child
node.ReplaceChild(old, newChild);  // swap a child
node.Clear();                      // remove all children

node.ParentNode;                   // parent (null if root)
node.ChildNodes;                   // List<Node>
node.PreviousSibling;
node.NextSibling;
node.RootNode;                     // walk up to root
```

### Query Selectors

Similar to CSS querySelector:

```csharp
Node? label  = root.QuerySelector("#label");         // by ID
Node? btn    = root.QuerySelector(".btn-primary");   // by class
Node? first  = root.QuerySelector("Node.active");    // by type + class

List<Node> all = root.QuerySelectorAll(".row");
```

### Style Properties — Complete Reference

#### Box Model
```csharp
Style = new Style
{
    Size    = new Size(width, height),  // 0 = auto-size
    Padding = new EdgeSize(top, right, bottom, left),
    Margin  = new EdgeSize(top, right, bottom, left),

    // Auto-sizing behavior
    AutoSize = AutoSize.Fit,   // shrink to content
    // AutoSize = AutoSize.Grow, // expand to fill available space

    // Layout flow
    Flow = Flow.Horizontal,  // children stack horizontally (default)
    // Flow = Flow.Vertical, // children stack vertically

    // Anchor within parent
    Anchor = AnchorPoint.MiddleLeft,
}
```

#### Background
```csharp
Style = new Style
{
    BackgroundColor          = new Color("#1A1A2E"),
    BackgroundGradient       = new GradientColor(color1, color2, GradientType.Vertical),
    BackgroundGradientInset  = new EdgeSize(2),
    BackgroundImage          = iconIdUint,        // game icon
    BackgroundImageInset     = new EdgeSize(4),
    BackgroundImageScale     = 1.0f,
    BackgroundImageColor     = new Color(1f, 1f, 1f, 0.5f),
    BackgroundImageRotation  = 0f,
    BackgroundImageBlendMode = BlendMode.Screen,
}
```

#### Border & Stroke
```csharp
Style = new Style
{
    BorderColor    = new BorderColor("#FF4444", "#FF4444", "#FF4444", "#FF4444"),
    BorderWidth    = new EdgeSize(1),
    BorderRadius   = 6f,
    BorderInset    = new EdgeSize(0),
    RoundedCorners = RoundedCorners.All,           // or combine flags

    StrokeColor  = new Color("#FFFFFF"),
    StrokeWidth  = new EdgeSize(1),
    StrokeInset  = new EdgeSize(0),
    StrokeRadius = 4f,
}
```

`RoundedCorners` flags:
```csharp
RoundedCorners.None        = 0
RoundedCorners.TopLeft     = 1
RoundedCorners.TopRight    = 2
RoundedCorners.BottomLeft  = 4
RoundedCorners.BottomRight = 8
RoundedCorners.All         = 15
```

#### Text
```csharp
Style = new Style
{
    Color        = new Color("#FFFFFF"),
    OutlineColor = new Color("#000000"),
    OutlineSize  = 1f,
    FontSize     = 13f,
    Font         = 0u,             // 0 = default; use Dalamud font IDs
    LineHeight   = 1.2f,
    TextAlign    = TextAlign.Left, // Left | Center | Right
    TextOffset   = new Point(0, 0),
    TextOverflow = TextOverflow.Ellipsis,
    MaxWidth     = 120f,
    WordWrap     = false,
    TextShadowSize  = 2f,
    TextShadowColor = new Color(0f, 0f, 0f, 0.8f),
}
```

#### Image / Icon
```csharp
Style = new Style
{
    IconId           = 60071u,         // game texture icon ID
    ImageInset       = new EdgeSize(2),
    ImageOffset      = new Point(0, 0),
    ImageScale       = 1.0f,
    ImageRotation    = 0f,
    ImageColor       = new Color(1f, 1f, 1f, 1f),
    ImageBlendMode   = BlendMode.SrcOver,
    ImageGrayscale   = false,
    ImageContrast    = 1.0f,
    ImageRounding    = 4f,
    ImageRoundedCorners = RoundedCorners.All,
    ImageBlur        = 0f,
    ImageScaleMode   = ImageScaleMode.Adapt,   // or Original
}
```

#### Visibility & Effects
```csharp
Style = new Style
{
    Opacity       = 1.0f,    // 0.0 transparent → 1.0 opaque
    IsAntialiased = true,
    DropShadow    = new ShadowDefinition { ... },
}
```

#### Transitions / Animations
```csharp
Style = new Style
{
    TransitionDuration    = 200u,                     // milliseconds
    TransitionType        = TransitionType.EaseOutQuad,
    TransitionAddClass    = "active",    // add this class when transition ends
    TransitionRemoveClass = "inactive",  // remove this class when transition ends
}
```

All 31 transition types:
```
Linear
EaseInSine      EaseOutSine      EaseInOutSine
EaseInQuad      EaseOutQuad      EaseInOutQuad
EaseInCubic     EaseOutCubic     EaseInOutCubic
EaseInQuart     EaseOutQuart     EaseInOutQuart
EaseInQuint     EaseOutQuint     EaseInOutQuint
EaseInExpo      EaseOutExpo      EaseInOutExpo
EaseInCirc      EaseOutCirc      EaseInOutCirc
EaseInBack      EaseOutBack      EaseInOutBack
EaseInElastic   EaseOutElastic   EaseInOutElastic
EaseInBounce    EaseOutBounce    EaseInOutBounce
```

### Node Events

```csharp
node.OnClick       += n => { /* left click */ };
node.OnDoubleClick += n => { /* double click */ };
node.OnMouseEnter  += n => { /* hover start */ };
node.OnMouseLeave  += n => { /* hover end */ };
node.OnDragStart   += n => { /* drag begins */ };
node.OnDragMove    += n => { /* drag in progress */ };
node.OnDragEnd     += n => { /* drag released */ };
```

### Drag-and-Drop Sorting (Built-In)

Una.Drawing has first-class drag sorting — no ImGui drag-drop workarounds needed:

```csharp
var list = new Node { Id = "list" };

foreach (var item in items)
{
    var row = new Node
    {
        Id              = $"row-{item.Id}",
        Sortable        = true,
        SortableGroupId = "my-list",  // rows with same group ID can be sorted together
    };
    row.OnSorted += (dragged, target) => { /* reorder logic */ };
    list.AppendChild(row);
}
```

### CSS-Like Stylesheets

Apply styles to all nodes matching a selector, like CSS:

```csharp
var stylesheet = new Stylesheet(new Dictionary<string, Style>
{
    [".row"] = new Style
    {
        Size    = new Size(0, 24),
        Padding = new EdgeSize(2, 8),
    },
    [".row:hover"] = new Style
    {
        BackgroundColor = new Color("#FFFFFF20"),
    },
    [".row.active"] = new Style
    {
        BackgroundColor = new Color("#4444FF40"),
        Color           = new Color("#FFFFFF"),
    },
    ["#header"] = new Style
    {
        FontSize = 16f,
        Color    = new Color("#FFAA00"),
    },
});

root.Stylesheet = stylesheet;
```

Pseudo-classes supported: `:hover`, `:active`, `:focus`, `:disabled`
Class toggling:
```csharp
node.ClassList.Add("active");
node.ClassList.Remove("active");
node.TagsList.Add("combat");   // tags propagate to children if InheritTags = true
```

---

## Umbra.Common Framework

Umbra uses its own lightweight DI container distinct from Dalamud's `[PluginService]`.

### Service Registration

```csharp
using Umbra.Common;

[Service]
public sealed class MyDataService
{
    // Dependencies injected via constructor
    public MyDataService(AnotherService dep) { ... }
}
```

### Lifecycle Attributes

```csharp
[WhenFrameworkCompiling(executionOrder: 0)]
public static void Initialize()
{
    // Sync startup — runs when Umbra framework compiles (plugin loaded)
    // executionOrder: lower numbers run first
}

[WhenFrameworkAsyncCompiling(executionOrder: 5)]
public static async Task InitializeAsync()
{
    // Async startup — for I/O or slow init
}

[WhenFrameworkDisposing]
public static void Cleanup()
{
    // Called on unload — clean up resources here
}
```

### Tick and Draw Scheduling

```csharp
[OnTick(intervalMs: 500)]
public static void OnTick()
{
    // Called every 500ms on the framework thread
}

[OnDraw]
public static void OnDraw()
{
    // Called every frame during the draw phase
}
```

### Resolving Services

```csharp
// Resolve a registered service
var myService = Framework.Service<MyDataService>();

// Instantiate with DI (fills constructor params from container)
var instance = Framework.InstantiateWithDependencies<MyWidget>();
```

### Config Variables in Umbra.Common

```csharp
public sealed class MyConfig
{
    [ConfigVariable("MyPlugin.ShowDps", min: 0, max: 1)]
    public static bool ShowDps { get; set; } = true;

    [ConfigVariable("MyPlugin.RefreshRate")]
    public static int RefreshRate { get; set; } = 500;
}
```

---

## Integration With DamageMeter

The most natural Umbra widgets for DamageMeter are:

### Option 1: Compact DPS Badge (Toolbar)
A single `StandardToolbarWidget` showing your own DPS as a number badge in the toolbar.
- `SetText()` = DPS value (e.g. `"12,450"`)
- `SetSubText()` = label (`"DPS"`) or encounter timer
- `SetGameIconId()` = crossed swords icon or job icon
- `SetProgressBarValue()` = your DPS as a fraction of top parse for the encounter

### Option 2: World Markers Over Party Members
A `WorldMarkerFactory` that places DPS labels above party members' heads in the world,
updating every tick with live combat data.

### Architectural Note
DamageMeter's existing `CombatTracker` service can be injected into your Umbra widget
only if it is also registered in Umbra.Common's DI container. Otherwise, pass data through
a shared static or via Dalamud IPC.

The cleanest pattern is a **thin Umbra adapter DLL** that:
1. References both `Umbra.dll` and `DamageMeter.dll`
2. Reads data from `CombatTracker` via a static accessor or IPC
3. Pushes that data into toolbar text or world markers

This keeps DamageMeter itself Umbra-agnostic (Umbra is optional for the user).

---

## Checklist: Shipping an Umbra Widget

- [ ] Create a separate plugin DLL (distinct from the main plugin)
- [ ] Reference `Umbra.dll` and `Umbra.Common.dll` with `Private="false"`
- [ ] Decorate widget class with `[ToolbarWidget(...)]` — unique ID, name, description, tags
- [ ] Extend `StandardToolbarWidget` (or `ToolbarWidget` for full custom nodes)
- [ ] Implement `OnLoad()`, `OnUnload()`, `OnDraw()`
- [ ] Return config variables from `GetConfigVariables()` (yield base first)
- [ ] Set `Author = "Sansflaire"` in the plugin manifest
- [ ] Add `CLAUDE.md` to `.gitignore`
- [ ] Test: install both main plugin and Umbra widget DLL → Umbra widget picker should list it
- [ ] Verify Umbra version path in `.csproj` when Umbra updates

---

## DLL Locations Reference

| Assembly | Path |
|----------|------|
| `Umbra.dll` | `%APPDATA%\XIVLauncher\installedPlugins\Umbra\3.1.12.0\Umbra.dll` |
| `Umbra.Common.dll` | `%APPDATA%\XIVLauncher\installedPlugins\Umbra\3.1.12.0\Umbra.Common.dll` |
| `Umbra.Game.dll` | `%APPDATA%\XIVLauncher\installedPlugins\Umbra\3.1.12.0\Umbra.Game.dll` |
| `Una.Drawing.dll` | `%APPDATA%\XIVLauncher\installedPlugins\Umbra\3.1.12.0\Una.Drawing.dll` |
| `Dalamud.dll` | `%APPDATA%\XIVLauncher\addon\Hooks\dev\Dalamud.dll` |

---

## References

- Umbra Source: https://github.com/una-xiv/umbra
- Umbra.Common Source: https://github.com/una-xiv/Umbra.Common
- Una.Drawing Source: https://github.com/una-xiv/drawing
- Sample Plugin: https://github.com/una-xiv/Umbra.SamplePlugin
- Glamourer Integration (real-world example): https://github.com/una-xiv/Umbra.Glamourer
- Official Docs: https://una-xiv.github.io/umbra-docs/
- SkiaSharp: https://github.com/mono/SkiaSharp
