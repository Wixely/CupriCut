using CupriFace;
using CupriFace.Components;
using SkiaSharp;

namespace CupriCut.Gui;

/// <summary>
/// The studio window's markup, style and model.
///
/// <para>CupriCut's own interface is drawn by the engine CupriCut renders with — which is not a
/// flourish but the reason this was cheap to build: the preview is an <c>ISurfaceSource</c>, the
/// same seam a video player uses, and the region drag is <c>OnPointer</c>, the same one a pinch
/// gesture uses. Nothing here needed anything the engine did not already have.</para>
///
/// <para>Split from <see cref="StudioController"/> on purpose. This is what the window IS; the
/// controller is what it DOES. The split is what lets the markup and the model be tested headlessly
/// — <c>doc.RenderToImage</c> on this app needs no window at all.</para>
///
/// <para>Three engine bugs were routed around while this was written — Wixely/CupriFace#161
/// (<c>align-items:center</c> did nothing to an auto-width item in a column flex container), #162
/// (percentage <c>border-radius</c> painted a square) and #163 (multi-value <c>border-radius</c>
/// was dropped entirely). All three are <b>fixed in CupriFace 0.24.1</b>, which this depends on.
/// The workarounds are gone from the markup; the layout still flexes rather than using fixed
/// columns, which was always the better shape and is unrelated to those bugs.</para>
/// </summary>
public sealed class StudioApp(StudioModel model) : CupriApp
{
    /// <summary>The key the preview surface is registered under, and the element's
    /// <c>data-cupri-surface</c>.</summary>
    public const string PreviewKey = "preview";

    /// <summary>The <c>data-</c> attribute the region drag is bound to.</summary>
    public const string MarkAttribute = "data-cut-mark";

    public override string Title => "CupriCut Studio";

    public override int Width => 1440;

    public override int Height => 860;

    public override SKColor Background => new(0x0F, 0x13, 0x1A);

    public override bool DarkWindowChrome => true;

    public override object Model => model;

    public override ComponentRegistry Components => ComponentRegistry.Default();

    // A render arriving on a worker thread marks the surface as changed, but a small heartbeat is
    // the belt to that braces: it costs nothing on an idle window and means the preview can never
    // sit stale because a repaint signal was missed.
    public override double RefreshIntervalSeconds => 0.2;

    // Presentation is the default, PresentInfo.Responsive: lay out at the window's LOGICAL size at
    // scale 1, so the window revealing more space reveals more content rather than bigger content.
    // Worth stating because the logical size is NOT the pixel size on a scaled display - this window
    // lays out at 1440x860 logical inside a 2182x1346 physical frame at 150%. A layout of fixed
    // pixel columns would be fine here and wrong on a smaller display, which is why the one below
    // flexes instead.

    public override string Html => """
        <div class="shell">

          <div class="bar">
            <div class="brand">CupriCut</div>
            <cupri-button data-cut-view="studio" class="nav {{StudioNavClass}}">Studio</cupri-button>
            <cupri-button data-cut-view="settings" class="nav {{SettingsNavClass}}">Settings</cupri-button>
            <div class="barsub">{{ServerState}}</div>
          </div>

          <div class="body {{StudioClass}}">

            <div class="rail">
              <div class="railhead">PROJECTS</div>
              <div data-repeat="Projects">
                <div class="prow {{RowClass}}" data-cut-open="{{File}}">
                  <div class="pname">{{Name}}</div>
                  <div class="pdetail">{{Detail}}</div>
                  <div class="pbadge {{BadgeClass}}">{{Badge}}</div>
                </div>
              </div>
              <div class="empty {{NoProjectsClass}}">No projects yet. Save one with save_project.</div>
            </div>

            <div class="main">
              <div class="maintop">
                <div class="ptitle">{{SelectedTitle}}</div>
                <div class="psize">{{SizeLabel}}</div>
              </div>

              <div class="stagewrap">
                <!-- The overlays are CHILDREN of the stage, not siblings. The engine has no
                     pointer-events, so an absolutely positioned sibling ON TOP of the stage
                     swallows the drag: the hit test returns the placeholder and the walk up its
                     ancestors never passes the element carrying data-cut-mark. Nested, the same
                     walk reaches the stage and the drag works wherever it lands. -->
                <div class="stage" data-cupri-surface="preview" data-cut-mark="preview">
                  <div class="placeholder {{PlaceholderClass}}">Select a project to preview a frame.</div>
                  <div class="spinner {{SpinnerClass}}">rendering&hellip;</div>
                </div>
              </div>

              <div class="scrub">
                <cupri-button data-cut-action="rewind" class="transport">&#9198;</cupri-button>
                <cupri-button data-cut-action="play" class="transport {{PlayClass}}">{{PlayLabel}}</cupri-button>
                <div class="tlabel">{{TimeLabel}}</div>
                <cupri-slider min="0" max="100" value="{{Scrub}}"></cupri-slider>
                <div class="tlabel right">{{DurationLabel}}</div>
              </div>

              <div class="tools">
                <cupri-button data-cut-action="mark" class="{{MarkClass}}">{{MarkLabel}}</cupri-button>
                <cupri-textfield value="{{PendingNote}}" placeholder="What is wrong with it?"></cupri-textfield>
                <cupri-button data-cut-action="cancel" class="{{CancelClass}}">Cancel</cupri-button>
              </div>

              <div class="status">{{Status}}</div>
              <div class="purity">{{RenderStat}} &nbsp; {{PurityNote}}</div>
            </div>

            <div class="notes">
              <div class="railhead">ANNOTATIONS &middot; {{OpenCount}} open</div>
              <div class="edit {{EditingClass}}">
                <div class="editlabel">Rewriting this note</div>
                <cupri-textfield value="{{EditingNote}}" placeholder="What is wrong with it?"></cupri-textfield>
                <div class="nactions">
                  <cupri-button data-cut-action="save-note">Save</cupri-button>
                  <cupri-button data-cut-action="cancel-note">Cancel</cupri-button>
                </div>
              </div>

              <div data-repeat="Annotations">
                <div class="nrow {{RowClass}} {{EditClass}}">
                  <div class="ntop">
                    <div class="nat">{{At}}</div>
                    <div class="nstatus">{{StatusLabel}}</div>
                  </div>
                  <div class="ntext">{{Note}}</div>
                  <div class="nregion">{{Region}}</div>
                  <div class="nframe">{{FrameAt}}</div>
                  <div class="nactions">
                    <cupri-button data-cut-goto="{{Id}}">Go to</cupri-button>
                    <cupri-button data-cut-edit="{{Id}}">Edit</cupri-button>
                    <cupri-button data-cut-delete="{{Id}}">Delete</cupri-button>
                  </div>
                </div>
              </div>
              <div class="empty {{NoAnnotationsClass}}">
                Nothing marked. Draw a box on the frame and an agent can read it back
                with list_annotations.
              </div>
            </div>

          </div>

          <div class="settings {{SettingsClass}}">
            <div class="tabs">
              <cupri-button data-cut-tab="server" class="tab {{ServerTabNav}}">MCP server</cupri-button>
              <cupri-button data-cut-tab="render" class="tab {{RenderTabNav}}">Rendering</cupri-button>
              <cupri-button data-cut-tab="calibrate" class="tab {{CalibrateTabNav}}">Calibrate</cupri-button>
            </div>

            <div class="pane {{ServerTabClass}}">
              <div class="sechead">ENDPOINT</div>
              <div class="row"><div class="key">MCP URL</div><div class="val mono">{{ServerUrl}}</div>
                <cupri-button data-cupri-copy="{{ServerUrl}}">Copy</cupri-button></div>
              <div class="row"><div class="key">Health</div><div class="val mono">{{HealthUrl}}</div>
                <cupri-button data-cupri-copy="{{HealthUrl}}">Copy</cupri-button></div>
              <div class="row"><div class="key">Status</div><div class="val">{{ServerState}}</div></div>
              <div class="row"><div class="key">Password</div><div class="val">{{PasswordState}}</div></div>
              <div class="hint">
                Point an MCP client at the URL above. It is the same server whether this window is
                open or CupriCut is run headless with -c.
              </div>
            </div>

            <div class="pane {{RenderTabClass}}">
              <div class="sechead">PATHS</div>
              <div class="row"><div class="key">Compositions</div><div class="val mono">{{CompositionRootPaths}}</div></div>
              <div class="row"><div class="key">Projects</div><div class="val mono">{{ProjectRootPath}}</div></div>
              <div class="row"><div class="key">Output</div><div class="val mono">{{OutputRootPath}}</div></div>
              <div class="row"><div class="key">ffmpeg</div><div class="val mono">{{FfmpegPath}}</div></div>
              <div class="sechead">RENDERING</div>
              <div class="row"><div class="key">Workers</div><div class="val">{{WorkersSetting}}</div></div>
              <div class="row"><div class="key">Engine</div><div class="val">{{EngineVersion}}</div></div>
              <div class="hint">
                These come from CupriCut.json. Calibrate measures the worker count for this machine.
              </div>
            </div>

            <div class="pane {{CalibrateTabClass}}">
              <div class="tools">
                <cupri-button data-cut-action="calibrate">Run calibration</cupri-button>
                <cupri-button data-cut-action="toggle-checks">{{ShowAllLabel}}</cupri-button>
                <cupri-button data-cut-action="apply-workers" class="{{ApplyWorkersClass}}">{{ApplyWorkersLabel}}</cupri-button>
                <div class="spin {{CalibratingClass}}">measuring&hellip;</div>
              </div>
              <div class="summary">{{CalibrationSummary}}</div>
              <div class="empty {{NoCalibrationClass}}">
                Nothing measured yet. Calibration encodes a two-frame clip per codec with the real
                arguments CupriCut uses, then times a render at several worker counts.
              </div>
              <div data-repeat="Calibration">
                <div class="chk {{RowClass}}">
                  <div class="chkline">
                    <div class="chkmark">{{Mark}}</div>
                    <div class="chkname">{{Group}} &middot; {{Name}}</div>
                    <div class="chkdetail">{{Detail}}</div>
                  </div>
                  <div class="chkfix {{FixClass}}">{{Fix}}</div>
                </div>
              </div>
            </div>
          </div>

        </div>
        """;

    public override string Css => """
        /* Fluid, not fixed. The window is laid out at its LOGICAL size, so on a 150% display a
           1500px window is 1000 logical pixels - a layout built from hardcoded 1500px columns
           overflows and clips its right-hand panel. The two rails keep fixed widths because a
           sidebar should; everything between them flexes. */
        .shell { width:100%; height:100%; display:flex; flex-direction:column;
                 font-family:"Noto Sans"; background:#0f131a; color:#e8edf5; }

        .bar { height:46px; display:flex; align-items:center; background:#141b26;
               border-bottom:1px solid #223047; }
        .brand { width:104px; margin-left:16px; font-size:16px; font-weight:700; color:#f4f6fb; }
        .barsub { flex:1; margin-left:16px; font-size:12px; color:#63718a; }

        .body { flex:1; display:flex; }

        .rail  { width:230px; background:#111823; border-right:1px solid #223047; overflow:scroll; }
        .notes { width:270px; background:#111823; border-left:1px solid #223047; overflow:scroll; }
        .railhead { margin:14px 0 8px 14px; font-size:11px; font-weight:700;
                    letter-spacing:2px; color:#5a6a85; }

        .prow { margin:0 10px 4px 10px; padding:9px 11px; border-radius:6px; background:#151d2a; }
        .prow.on { background:#1d2a3f; border:1px solid #2f4463; }
        .pname   { font-size:13px; font-weight:700; color:#e8edf5; }
        .pdetail { font-size:11px; color:#63718a; margin-top:3px; }
        .pbadge  { font-size:11px; font-weight:700; color:#d9642a; margin-top:4px; }

        .main { flex:1; display:flex; flex-direction:column; }
        .maintop { display:flex; align-items:center; margin:14px 18px 0 18px; }
        .ptitle { flex:1; font-size:17px; font-weight:700; color:#f4f6fb; }
        .psize  { width:110px; font-size:11px; color:#63718a; }

        /* The preview takes whatever vertical space is left; the surface fills it.
           position:relative so the two overlays below can sit ON the stage rather than in the
           column with it - a negative margin put them in the flow, so hiding the placeholder when
           the first frame arrived RESIZED the stage under the pointer. A box that changes size the
           moment a preview lands is a box you cannot draw an accurate region on. */
        .stagewrap { flex:1; margin:12px 18px 0 18px; background:#0a0d13;
                     border:1px solid #223047; border-radius:6px; position:relative; }
        .stage { width:100%; height:100%; }
        .placeholder { position:absolute; left:18px; top:50%; font-size:13px; color:#3f4c63; }
        .spinner { position:absolute; left:18px; bottom:10px; font-size:11px; color:#d9642a; }

        .scrub { display:flex; align-items:center; margin:12px 18px 0 18px; }
        .tlabel { width:58px; font-size:12px; font-weight:700; color:#8b98ad; }
        .tlabel.right { width:52px; color:#5a6a85; }
        .cupri-slider { flex:1; }

        .tools { display:flex; align-items:center; margin:10px 18px 0 18px; }
        .tools .cupri-button { margin-right:8px; }
        .tools .cupri-textfield { flex:1; }
        .armed { background:#d9642a; }

        .status { margin:10px 18px 2px 18px; font-size:11px; color:#63718a; }
        .purity { margin:0 18px 12px 18px; font-size:11px; color:#44506680; color:#455066; }
        .transport { margin-right:8px; }

        .nrow { margin:0 10px 8px 10px; padding:9px 11px; background:#151d2a; border-radius:6px; }
        .nrow.done { background:#121a25; }
        .ntop { display:flex; align-items:center; }
        .nat  { flex:1; font-size:11px; font-weight:700; color:#d9642a; }
        .nstatus { width:62px; font-size:11px; color:#5a6a85; }
        .ntext   { font-size:12px; color:#e8edf5; margin-top:5px; }
        .nregion { font-size:11px; color:#5a6a85; margin-top:4px; }
        .nframe  { font-size:11px; color:#455066; margin-top:2px; }
        .nrow.editing { border:1px solid #d9642a; }
        .edit { margin:0 10px 10px 10px; padding:10px; background:#1d2a3f; border-radius:6px; }
        .editlabel { font-size:11px; font-weight:700; color:#d9642a; margin-bottom:6px; }
        .edit .cupri-textfield { width:244px; }
        .nactions { margin-top:8px; display:flex; }
        .nactions .cupri-button { margin-right:6px; }

        .empty { margin:8px 14px 0 14px; font-size:12px; color:#3f4c63; }

        .cupri-button { background:#223047; color:#e8edf5; border-radius:5px;
                        padding:6px 11px; font-size:12px; }

        /* ---- navigation and settings ---------------------------------------------------- */

        .nav   { margin-left:8px; background:transparent; color:#8b98ad; }
        .navon { background:#223047; color:#f4f6fb; }

        .settings { flex:1; overflow:scroll; }

        .tabs { display:flex; align-items:center; margin:16px 0 0 24px; }
        .tab   { margin-right:8px; background:#151d2a; color:#8b98ad; }
        .tabon { background:#223047; color:#f4f6fb; }

        .pane { margin:18px 24px 24px 24px; }
        .sechead { font-size:11px; font-weight:700; letter-spacing:2px; color:#5a6a85;
                   margin:0 0 10px 0; }

        .row  { display:flex; align-items:center; margin-bottom:8px; }
        .key  { width:130px; font-size:12px; color:#8b98ad; }
        .val  { flex:1; font-size:13px; color:#e8edf5; }
        .mono { font-size:12px; color:#c6d2e3; }

        .hint { margin-top:14px; font-size:12px; color:#5a6a85; }

        .summary { margin:14px 0 12px 0; font-size:13px; color:#e8edf5; }
        .spin    { font-size:12px; color:#d9642a; }

        .chk      { margin-bottom:6px; padding:8px 10px; background:#151d2a; border-radius:5px; }
        .chk.fail { background:#2a1b1b; }
        .chkline  { display:flex; align-items:center; }
        .chkmark  { width:46px; font-size:11px; font-weight:700; color:#4ec9b0; }
        .chk.fail .chkmark { color:#e8654a; }
        .chkname  { width:230px; font-size:12px; font-weight:700; color:#e8edf5; }
        .chkdetail{ flex:1; font-size:12px; color:#8b98ad; }
        .chkfix   { margin-top:6px; font-size:12px; color:#d9642a; }

        /* ---- last on purpose -------------------------------------------------------------
           The engine has no conditional attribute, so a computed class on the model is how
           anything is hidden. It has to come LAST: these are all single-class selectors, so
           specificity ties and the later rule wins - declared at the top, `.body { display:flex }`
           beat it and the studio page stayed visible underneath the settings page. */
        .hidden { display:none; }
        """;

    /// <summary>Fonts come from the same directories the renderer registers, so the window's text
    /// and a rendered frame's text are the same faces.</summary>
    public override IEnumerable<CupriFace.Resources.CupriSource> Fonts => FontSources;

    /// <summary>Set by the controller before the window opens.</summary>
    public IList<CupriFace.Resources.CupriSource> FontSources { get; init; } = [];
}
