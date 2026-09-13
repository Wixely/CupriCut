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

    public override int Width => 1500;

    public override int Height => 900;

    public override SKColor Background => new(0x0F, 0x13, 0x1A);

    public override bool DarkWindowChrome => true;

    public override object Model => model;

    public override ComponentRegistry Components => ComponentRegistry.Default();

    // A render arriving on a worker thread marks the surface as changed, but a small heartbeat is
    // the belt to that braces: it costs nothing on an idle window and means the preview can never
    // sit stale because a repaint signal was missed.
    public override double RefreshIntervalSeconds => 0.2;

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

        .shell { width:100%; height:100%; display:flex; flex-direction:column;
                 font-family:"Noto Sans"; background:#0f131a; color:#e8edf5; }

        .bar { height:52px; display:flex; align-items:center; background:#141b26;
               border-bottom:1px solid #223047; }
        .brand { width:120px; margin-left:18px; font-size:17px; font-weight:700; color:#f4f6fb; }
        .barsub { width:900px; font-size:13px; color:#63718a; }

        .body { display:flex; height:848px; }

        .rail  { width:280px; height:848px; background:#111823; border-right:1px solid #223047;
                 overflow:scroll; }
        .notes { width:320px; height:848px; background:#111823; border-left:1px solid #223047;
                 overflow:scroll; }
        .railhead { width:280px; margin:16px 0 10px 16px; font-size:11px; font-weight:700;
                    letter-spacing:2px; color:#5a6a85; }

        .prow { width:248px; margin:0 0 4px 12px; padding:10px 12px; border-radius:6px;
                background:#151d2a; }
        .prow.on { background:#1d2a3f; border:1px solid #2f4463; }
        .pname   { width:220px; font-size:14px; font-weight:700; color:#e8edf5; }
        .pdetail { width:220px; font-size:12px; color:#63718a; margin-top:3px; }
        .pbadge  { width:220px; font-size:11px; font-weight:700; color:#d9642a; margin-top:4px; }

        .main { width:900px; height:848px; }
        .maintop { width:860px; margin:16px 0 0 20px; display:flex; align-items:center; }
        .ptitle { width:700px; font-size:19px; font-weight:700; color:#f4f6fb; }
        .psize  { width:140px; font-size:12px; color:#63718a; }

        /* The frame box is a fixed 16:9 the surface fills, so normalising a drag against it is
           arithmetic rather than guesswork. */
        .stagewrap { width:860px; height:484px; margin:14px 0 0 20px; background:#0a0d13;
                     border:1px solid #223047; border-radius:6px; }
        .stage { width:858px; height:482px; }
        .placeholder { width:858px; margin-top:-260px; font-size:14px; color:#3f4c63; }
        .spinner { width:858px; margin-top:-20px; font-size:12px; color:#d9642a; }

        .scrub { width:860px; margin:16px 0 0 20px; display:flex; align-items:center; }
        .tlabel { width:70px; font-size:13px; font-weight:700; color:#8b98ad; }
        .tlabel.right { color:#5a6a85; }
        .cupri-slider { width:700px; }

        .tools { width:860px; margin:14px 0 0 20px; display:flex; align-items:center; }
        .tools .cupri-button { margin-right:10px; }
        .tools .cupri-textfield { width:420px; }
        .armed { background:#d9642a; }

        .status { width:860px; margin:14px 0 0 20px; font-size:12px; color:#63718a; }

        .nrow { width:288px; margin:0 0 8px 16px; padding:10px 12px; background:#151d2a;
                border-radius:6px; }
        .nrow.done { background:#121a25; }
        .ntop { width:264px; display:flex; align-items:center; }
        .nat  { width:180px; font-size:12px; font-weight:700; color:#d9642a; }
        .nstatus { width:80px; font-size:11px; color:#5a6a85; }
        .ntext   { width:264px; font-size:13px; color:#e8edf5; margin-top:5px; }
        .nregion { width:264px; font-size:11px; color:#5a6a85; margin-top:4px; }
        .nactions { width:264px; margin-top:8px; display:flex; }
        .nactions .cupri-button { margin-right:6px; }

        .empty { width:250px; margin:10px 0 0 16px; font-size:12px; color:#3f4c63; }

        .cupri-button { background:#223047; color:#e8edf5; border-radius:5px;
                        padding:7px 12px; font-size:12px; }
        """;

    /// <summary>Fonts come from the same directories the renderer registers, so the window's text
    /// and a rendered frame's text are the same faces.</summary>
    public override IEnumerable<CupriFace.Resources.CupriSource> Fonts => FontSources;

    /// <summary>Set by the controller before the window opens.</summary>
    public IList<CupriFace.Resources.CupriSource> FontSources { get; init; } = [];
}
