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
/// <para>Three engine bugs are routed around deliberately, all filed from this repository:
/// Wixely/CupriFace#161 (<c>align-items:center</c> does nothing to an auto-width item in a column
/// flex container — so everything centred here has an explicit width), #162 (percentage
/// <c>border-radius</c> renders square) and #163 (multi-value <c>border-radius</c> is dropped
/// entirely — so every radius here is a single px value).</para>
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
            <div class="barsub">Studio &middot; the MCP server is running on this window</div>
          </div>

          <div class="body">

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
                <div class="stage" data-cupri-surface="preview" data-cut-mark="preview"></div>
                <div class="placeholder {{PlaceholderClass}}">Select a project to preview a frame.</div>
                <div class="spinner {{SpinnerClass}}">rendering&hellip;</div>
              </div>

              <div class="scrub">
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
            </div>

            <div class="notes">
              <div class="railhead">ANNOTATIONS &middot; {{OpenCount}} open</div>
              <div data-repeat="Annotations">
                <div class="nrow {{RowClass}}">
                  <div class="ntop">
                    <div class="nat">{{At}}</div>
                    <div class="nstatus">{{StatusLabel}}</div>
                  </div>
                  <div class="ntext">{{Note}}</div>
                  <div class="nregion">{{Region}}</div>
                  <div class="nactions">
                    <cupri-button data-cut-goto="{{Id}}">Go to</cupri-button>
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
        </div>
        """;

    public override string Css => """
        /* The engine has no conditional attribute, so "hidden" is how the model hides things. */
        .hidden { display:none; }

        /* Fluid, not fixed. The window is laid out at its LOGICAL size, so on a 150% display a
           1500px window is 1000 logical pixels - a layout built from hardcoded 1500px columns
           overflows and clips its right-hand panel. The two rails keep fixed widths because a
           sidebar should; everything between them flexes. */
        .shell { width:100%; height:100%; display:flex; flex-direction:column;
                 font-family:"Noto Sans"; background:#0f131a; color:#e8edf5; }

        .bar { height:46px; display:flex; align-items:center; background:#141b26;
               border-bottom:1px solid #223047; }
        .brand { width:104px; margin-left:16px; font-size:16px; font-weight:700; color:#f4f6fb; }
        .barsub { flex:1; font-size:12px; color:#63718a; }

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

        /* The preview takes whatever vertical space is left; the surface fills it. */
        .stagewrap { flex:1; margin:12px 18px 0 18px; background:#0a0d13;
                     border:1px solid #223047; border-radius:6px; }
        .stage { width:100%; height:100%; }
        .placeholder { margin:-260px 0 0 18px; font-size:13px; color:#3f4c63; }
        .spinner { margin:-18px 0 0 18px; font-size:11px; color:#d9642a; }

        .scrub { display:flex; align-items:center; margin:12px 18px 0 18px; }
        .tlabel { width:58px; font-size:12px; font-weight:700; color:#8b98ad; }
        .tlabel.right { width:52px; color:#5a6a85; }
        .cupri-slider { flex:1; }

        .tools { display:flex; align-items:center; margin:10px 18px 0 18px; }
        .tools .cupri-button { margin-right:8px; }
        .tools .cupri-textfield { flex:1; }
        .armed { background:#d9642a; }

        .status { margin:10px 18px 12px 18px; font-size:11px; color:#63718a; }

        .nrow { margin:0 10px 8px 10px; padding:9px 11px; background:#151d2a; border-radius:6px; }
        .nrow.done { background:#121a25; }
        .ntop { display:flex; align-items:center; }
        .nat  { flex:1; font-size:11px; font-weight:700; color:#d9642a; }
        .nstatus { width:62px; font-size:11px; color:#5a6a85; }
        .ntext   { font-size:12px; color:#e8edf5; margin-top:5px; }
        .nregion { font-size:11px; color:#5a6a85; margin-top:4px; }
        .nactions { margin-top:8px; display:flex; }
        .nactions .cupri-button { margin-right:6px; }

        .empty { margin:8px 14px 0 14px; font-size:12px; color:#3f4c63; }

        .cupri-button { background:#223047; color:#e8edf5; border-radius:5px;
                        padding:6px 11px; font-size:12px; }
        """;

    /// <summary>Fonts come from the same directories the renderer registers, so the window's text
    /// and a rendered frame's text are the same faces.</summary>
    public override IEnumerable<CupriFace.Resources.CupriSource> Fonts => FontSources;

    /// <summary>Set by the controller before the window opens.</summary>
    public IList<CupriFace.Resources.CupriSource> FontSources { get; init; } = [];
}
