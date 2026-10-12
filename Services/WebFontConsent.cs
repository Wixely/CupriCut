using CupriLex.Compiler;

namespace CupriCut.Services;

/// <summary>
/// What CupriCut will let a translation fetch: faces and the stylesheets that name them, and
/// nothing else, and only when <c>Cut:AllowWebFonts</c> says so.
///
/// <para><b>Its own type rather than <c>Consent.All</c> or a host list.</b> The compiler's built-in
/// policies are "everything", "nothing" and "these hosts", and none of them says the thing that is
/// actually true here. A host list would be a guess about which font services exist, and would let
/// a video through from an approved host. "Everything" would let a document pull media and
/// stylesheets from anywhere it liked. The real policy is about the KIND of request, which is the
/// one axis the built-ins do not offer, so it is written down rather than approximated.</para>
///
/// <para>Why fonts and not images: a missing image is a visible hole somebody can fix by dropping
/// the file in, and the markup still names it. A missing <i>face</i> is not recoverable that way -
/// under <c>FontPolicy.RegisteredOnly</c> the family resolves to nothing, the stack is trimmed to
/// a generic, and the composition is silently set in a typeface nobody chose. That asymmetry is
/// the whole reason this exists.</para>
/// </summary>
internal sealed class WebFontConsent : IConsent
{
    /// <summary>The instance used when the option is on. Stateless, so one is enough.</summary>
    public static readonly WebFontConsent Instance = new();

    private WebFontConsent() { }

    public bool Allows(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Stylesheet because a font service answers with CSS first and the faces are named inside
        // it; the compiler fetches a stylesheet only while looking for faces, and says so in the
        // outcome it reports back. Media and Other are refused outright.
        return request.Kind is Fetches.Font or Fetches.Stylesheet;
    }
}
