namespace Partner.Core.Agent.Web;

/// <summary>
/// The third quality gate: one ranked list out of several, without scoring anything by meaning.
/// <para>
/// It trusts two things only — the rank an engine gave a result, and how many engines independently returned it.
/// Reciprocal rank fusion is the standard way to combine those and it needs no model, no embedding and no
/// per-result judgement, which is exactly why it is here: this list has to be explainable to the user as
/// "these two sources ranked it highly", and a lexical score could not be explained that way.
/// </para>
/// </summary>
public sealed class Merger
{
    /// <summary>
    /// The fusion constant. 60 is the value from the original reciprocal-rank-fusion write-up and the one in
    /// near-universal use; it damps the difference between ranks 1 and 5 hard enough that agreement between two
    /// engines outweighs being first on one. Kept as a named constant so the replay fixtures, once they exist, can
    /// argue with it in one place.
    /// </summary>
    public const double RrfK = 60;

    /// <summary>
    /// An engine that indexes the kind being asked about, rather than the whole web. Its first hit is not worth
    /// more than anyone else's <i>because it says so</i>, but it is worth more: it was asked precisely because it
    /// indexes this. 1.5 means one expert hit outranks two base hits ranked below it and does not outrank three.
    /// </summary>
    public const double SpecialistWeight = 1.5;

    /// <summary>
    /// A looked-up record — a temperature, a version. It answers the question rather than pointing at a page that
    /// might, so one of them outweighs the whole default pool agreeing. Weight as documented in the plan's §4.6.
    /// </summary>
    public const double RecordWeight = 3.0;

    /// <summary>Everything else.</summary>
    public const double BaseWeight = 1.0;

    /// <summary>
    /// How similar two titles must be to be the same page. 0.8 on token overlap merges a repost and its AMP
    /// variant, which differ by a suffix at most, and leaves two different pages about the same subject apart.
    /// </summary>
    public const double TitleSimilarityThreshold = 0.8;

    public IReadOnlyList<SearchHit> Merge(IEnumerable<SearchHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);

        var groups = new List<Group>();

        foreach (var hit in hits)
        {
            var group = groups.FirstOrDefault(candidate => candidate.Accepts(hit));
            if (group is null)
            {
                groups.Add(new Group(hit));
            }
            else
            {
                group.Add(hit);
            }
        }

        return groups
            .Select(group => group.Merged())
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Final.Rank)
            .Select((hit, index) => hit.Final with { Rank = index })
            .ToList();
    }

    /// <summary>What one result is one engine's verdict of.</summary>
    private sealed class Group
    {
        private readonly string _identity;
        private readonly List<string> _titles = [];
        private readonly HashSet<string> _sources = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _weights = new(StringComparer.Ordinal);

        private SearchHit _best;
        private double _score;
        private string _snippet;
        private SearchHit _date;
        private string _url;

        public Group(SearchHit hit)
        {
            _identity = UrlIdentity(hit.Url);
            _best = hit;
            _snippet = hit.Snippet;
            _date = hit;
            _url = hit.Url;
            Add(hit);
        }

        public bool Accepts(SearchHit hit)
        {
            if (UrlIdentity(hit.Url) == _identity)
            {
                return true;
            }

            return _titles.Any(title => Similar(title, hit.Title) && SameNumbers(title, hit.Title));
        }

        public void Add(SearchHit hit)
        {
            var id = hit.Engine;
            if (!_weights.ContainsKey(id))
            {
                _weights[id] = WeightOf(hit.Engine);
            }

            _score += _weights[id] / (RrfK + Math.Max(0, hit.Rank));
            _titles.Add(hit.Title);
            _sources.Add(hit.Engine);

            if (hit.Rank < _best.Rank)
            {
                _best = hit;
            }

            if (hit.Snippet.Length > _snippet.Length)
            {
                _snippet = hit.Snippet;
            }

            if (BetterDate(hit, _date))
            {
                _date = hit;
            }

            if (Prefer(hit.Url, _url))
            {
                _url = hit.Url;
            }
        }

        public (SearchHit Final, double Score) Merged() =>
            (_best with
            {
                Title = _best.Title,
                Url = _url,
                Snippet = _snippet,
                Published = _date.Published,
                DateSource = _date.DateSource,
                Sources = _sources.OrderBy(source => source, StringComparer.Ordinal).ToList(),
            },
            _score);

        /// <summary>
        /// The date is taken from the result that knows it first-hand, not from whichever came first: a structured
        /// field beats a relative phrase beats a date scraped out of prose beats nothing.
        /// </summary>
        private static bool BetterDate(SearchHit candidate, SearchHit current)
        {
            if (candidate.Published is null)
            {
                return false;
            }

            if (current.Published is null)
            {
                return true;
            }

            var candidateRank = Priority(candidate.DateSource);
            var currentRank = Priority(current.DateSource);

            return candidateRank != currentRank
                ? candidateRank < currentRank

                // Same standing: the earlier date is the original and the later one is a repost's timestamp.
                : candidate.Published < current.Published;
        }

        private static int Priority(DateSource source) => source switch
        {
            DateSource.Structured => 0,
            DateSource.Relative => 1,
            DateSource.Text => 2,
            _ => 3,
        };

        /// <summary>https over http when the same page offers both.</summary>
        private static bool Prefer(string candidate, string current) =>
            candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !current.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What one engine's result on this page is worth in the fusion.</summary>
    private static double WeightOf(string engineId) => WebEngines.Find(engineId)?.Traits switch
    {
        { Nature: ResultNature.Record } => RecordWeight,
        { Role: EnginePoolRole.Specialist } => SpecialistWeight,
        _ => BaseWeight,
    };

    /// <summary>
    /// A page's identity, for grouping. Protocol, <c>www.</c>, the default port, the fragment and a trailing
    /// slash are all spelling. A country domain is not — <c>example.cn</c> and <c>example.com</c> are two
    /// publishers — and neither is a tracking parameter: stripping <c>?id=7</c> would merge two different
    /// articles on a site that serves its articles that way.
    /// </summary>
    public static string UrlIdentity(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url.Trim().TrimEnd('/').ToLowerInvariant();
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal) && host.Length > 4)
        {
            host = host[4..];
        }

        var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var path = uri.AbsolutePath.TrimEnd('/');

        return $"{host}{port}{path}{uri.Query}";
    }

    /// <summary>Token overlap between two titles, 0 when they share nothing.</summary>
    public static double TitleSimilarity(string left, string right)
    {
        var a = TitleTokens(left);
        var b = TitleTokens(right);

        if (a.Count == 0 || b.Count == 0)
        {
            return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        var shared = a.Count(token => b.Contains(token));
        var union = a.Count + b.Count - shared;

        return union == 0 ? 1 : (double)shared / union;
    }

    private static bool Similar(string left, string right) => TitleSimilarity(left, right) >= TitleSimilarityThreshold;

    /// <summary>
    /// Whether two titles carry the same numbers. "Node 20 released" and "Node 18 released" are 0.8 similar and
    /// are different results; merging them would answer the question with the wrong version, which is the single
    /// most damaging thing a deduplicator can do.
    /// </summary>
    private static bool SameNumbers(string left, string right)
    {
        var a = Numbers(left);
        var b = Numbers(right);

        return a.Count == b.Count && a.OrderBy(n => n, StringComparer.Ordinal)
            .SequenceEqual(b.OrderBy(n => n, StringComparer.Ordinal));
    }

    private static List<string> Numbers(string title)
    {
        var numbers = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var character in title)
        {
            if (char.IsDigit(character))
            {
                current.Append(character);
            }
            else if (current.Length > 0)
            {
                numbers.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            numbers.Add(current.ToString());
        }

        return numbers;
    }

    /// <summary>
    /// Titles as tokens: Latin words split on punctuation, CJK characters and their pairs. Version strings are
    /// left intact as tokens, because "5.0" and "5" are not the same release.
    /// </summary>
    private static HashSet<string> TitleTokens(string title) =>
        new(QueryTokens.Of(title).Select(token => token.ToLowerInvariant()), StringComparer.Ordinal);
}
